#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "capture_native.h"

#include <windows.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <roapi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <limits>
#include <memory>
#include <mutex>
#include <utility>
#include <vector>

namespace {

using winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool;
using winrt::Windows::Graphics::Capture::GraphicsCaptureItem;
using winrt::Windows::Graphics::Capture::GraphicsCaptureSession;
using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;

constexpr int kMinAccentScore = 90;
constexpr int kRidgeHalfWindow = 3;
constexpr float kImpulseThresholdPx = 14.0f;
constexpr float kMinReliableCoverage = 0.25f;
constexpr int kRidgeWindowPx = 32;
constexpr float kAccentAdaptRate = 0.10f;
constexpr int kAccentDominanceMin = 20;
constexpr int kAccentDominanceRatio = 40;
constexpr int64_t kMaxUpdateIntervalTicks = 5LL * 10'000'000LL;
constexpr size_t kUpdateIntervalWindow = 16;
constexpr int64_t kChangeDiffThreshold = 20'000;

struct Pixel {
    uint8_t b;
    uint8_t g;
    uint8_t r;
};

struct ChangeSample {
    bool content_changed;
    bool timing_changed;
};

struct BgraView {
    const uint8_t* data = nullptr;
    int32_t source_width = 0;
    int32_t source_height = 0;
    uint32_t row_pitch = 0;
    int32_t output_width = 0;
    int32_t output_height = 0;

    Pixel at(int32_t x, int32_t y) const noexcept {
        const auto sx = std::min(
            source_width - 1,
            static_cast<int32_t>(
                static_cast<int64_t>(x) * source_width / output_width));
        const auto sy = std::min(
            source_height - 1,
            static_cast<int32_t>(
                static_cast<int64_t>(y) * source_height / output_height));
        const auto* pixel = data + (static_cast<size_t>(sy) * row_pitch)
            + (static_cast<size_t>(sx) * 4U);
        return {pixel[0], pixel[1], pixel[2]};
    }
};

class HeightExtractor final {
public:
    HeightExtractor(
        int32_t inset_left,
        int32_t inset_top,
        int32_t inset_right,
        int32_t inset_bottom,
        int32_t smooth_radius) noexcept
        : inset_left_(inset_left),
          inset_top_(inset_top),
          inset_right_(inset_right),
          inset_bottom_(inset_bottom),
          smooth_radius_(std::max(0, smooth_radius)) {}

    int32_t plot_width(int32_t frame_width) const noexcept {
        return std::max(1, frame_width - inset_left_ - inset_right_);
    }

    bool extract(
        const BgraView& frame,
        std::vector<float>& out_y,
        uint8_t& out_b,
        uint8_t& out_g,
        uint8_t& out_r) {
        const auto width = frame.output_width;
        const auto height = frame.output_height;
        if (frame.data == nullptr || width < 16 || height < 16) {
            return false;
        }

        const auto plot_w = plot_width(width);
        const auto plot_h = std::max(1, height - inset_top_ - inset_bottom_);
        if (plot_w < 8 || plot_h < 8) {
            return false;
        }

        const auto nan = std::numeric_limits<float>::quiet_NaN();
        raw_.assign(static_cast<size_t>(plot_w), nan);
        int32_t detected = 0;
        int64_t accent_weight = 0;
        int64_t accent_b = 0;
        int64_t accent_g = 0;
        int64_t accent_r = 0;
        const auto y_top = inset_top_;
        const auto y_bottom = inset_top_ + plot_h - 1;
        auto last_ridge_y = nan;

        for (int32_t x = 0; x < plot_w; ++x) {
            const auto frame_x = inset_left_ + x;
            RidgeSample sample{};
            auto found = false;
            if (!std::isnan(last_ridge_y)) {
                const auto window_top = std::max(
                    y_top,
                    static_cast<int32_t>(last_ridge_y) - kRidgeWindowPx);
                const auto window_bottom = std::min(
                    y_bottom,
                    static_cast<int32_t>(last_ridge_y) + kRidgeWindowPx);
                found = try_sample_ridge(
                    frame,
                    frame_x,
                    window_top,
                    window_bottom,
                    sample);
            }

            if (!found) {
                found = try_sample_ridge(frame, frame_x, y_top, y_bottom, sample);
            }

            if (!found) {
                continue;
            }

            raw_[static_cast<size_t>(x)] = sample.y;
            ++detected;
            accent_weight += sample.peak_score;
            accent_b += static_cast<int64_t>(sample.color.b) * sample.peak_score;
            accent_g += static_cast<int64_t>(sample.color.g) * sample.peak_score;
            accent_r += static_cast<int64_t>(sample.color.r) * sample.peak_score;
            last_ridge_y = sample.y;
        }

        const auto reliable_columns = std::max(
            4,
            static_cast<int32_t>(plot_w * kMinReliableCoverage));
        if (detected < reliable_columns) {
            if (++accent_fail_streak_ >= 30) {
                accent_valid_ = false;
            }

            const auto floor_y = static_cast<float>(inset_top_ + plot_h - 1);
            for (auto& y : raw_) {
                if (std::isnan(y)) {
                    y = floor_y;
                }
            }
        } else {
            accent_fail_streak_ = 0;
        }

        interpolate_missing();
        despike();
        smooth();

        out_b = 212;
        out_g = 120;
        out_r = 0;
        if (accent_weight > 0) {
            out_b = static_cast<uint8_t>(
                std::clamp<int64_t>(accent_b / accent_weight, 0, 255));
            out_g = static_cast<uint8_t>(
                std::clamp<int64_t>(accent_g / accent_weight, 0, 255));
            out_r = static_cast<uint8_t>(
                std::clamp<int64_t>(accent_r / accent_weight, 0, 255));
            update_accent_target(out_b, out_g, out_r, accent_weight);
        }

        out_y = smooth_;
        return true;
    }

private:
    struct RidgeSample {
        float y = 0;
        Pixel color{};
        int32_t peak_score = 0;
    };

    static int32_t mid_of_three(int32_t a, int32_t b, int32_t c) noexcept {
        return std::max(std::min(a, b), std::min(std::max(a, b), c));
    }

    int32_t accent_score(Pixel pixel) const noexcept {
        const auto b = static_cast<int32_t>(pixel.b);
        const auto g = static_cast<int32_t>(pixel.g);
        const auto r = static_cast<int32_t>(pixel.r);
        const auto max_pixel = std::max({b, g, r});
        const auto min_pixel = std::min({b, g, r});
        const auto saturation = max_pixel - min_pixel;
        if (saturation < 40) {
            return 0;
        }

        if (!accent_valid_) {
            return saturation * 2 + max_pixel / 3;
        }

        const auto target_b = static_cast<int32_t>(accent_b_);
        const auto target_g = static_cast<int32_t>(accent_g_);
        const auto target_r = static_cast<int32_t>(accent_r_);
        const auto target_max = std::max({target_b, target_g, target_r});
        if ((target_max == target_b && (b < g || b < r))
            || (target_max == target_g && (g < b || g < r))
            || (target_max == target_r && (r < b || r < g))) {
            return 0;
        }

        const auto second = mid_of_three(b, g, r);
        const auto target_second = mid_of_three(target_b, target_g, target_r);
        const auto chroma = max_pixel - second;
        if (chroma * 100 < (target_max - target_second) * kAccentDominanceRatio) {
            return 0;
        }

        return chroma * 2 + saturation + max_pixel / 3;
    }

    bool try_sample_ridge(
        const BgraView& frame,
        int32_t x,
        int32_t y_top,
        int32_t y_bottom,
        RidgeSample& out_sample) const noexcept {
        auto max_score = 0;
        auto best_y = y_bottom;
        Pixel best_color{};
        for (auto y = y_top; y <= y_bottom; ++y) {
            const auto color = frame.at(x, y);
            const auto score = accent_score(color);
            if (score > max_score) {
                max_score = score;
                best_y = y;
                best_color = color;
            }
        }

        if (max_score < kMinAccentScore) {
            return false;
        }

        const auto threshold = std::max(kMinAccentScore, max_score * 85 / 100);
        const auto sample_top = std::max(y_top, best_y - kRidgeHalfWindow);
        const auto sample_bottom = std::min(y_bottom, best_y + kRidgeHalfWindow);
        int64_t score_sum = 0;
        int64_t weighted_y = 0;
        for (auto y = sample_top; y <= sample_bottom; ++y) {
            const auto score = accent_score(frame.at(x, y));
            if (score < threshold) {
                continue;
            }

            score_sum += score;
            weighted_y += static_cast<int64_t>(score) * y;
        }

        out_sample.y = static_cast<float>(weighted_y) / static_cast<float>(score_sum);
        out_sample.color = best_color;
        out_sample.peak_score = max_score;
        return true;
    }

    void update_accent_target(
        uint8_t candidate_b,
        uint8_t candidate_g,
        uint8_t candidate_r,
        int64_t weight) noexcept {
        if (weight < 64) {
            return;
        }

        const auto max_value = std::max({candidate_b, candidate_g, candidate_r});
        const auto min_value = std::min({candidate_b, candidate_g, candidate_r});
        if (max_value - min_value < kAccentDominanceMin) {
            return;
        }

        if (!accent_valid_) {
            accent_b_ = candidate_b;
            accent_g_ = candidate_g;
            accent_r_ = candidate_r;
            accent_valid_ = true;
            return;
        }

        accent_b_ = static_cast<uint8_t>(
            accent_b_ + (static_cast<int32_t>(candidate_b) - accent_b_) * kAccentAdaptRate);
        accent_g_ = static_cast<uint8_t>(
            accent_g_ + (static_cast<int32_t>(candidate_g) - accent_g_) * kAccentAdaptRate);
        accent_r_ = static_cast<uint8_t>(
            accent_r_ + (static_cast<int32_t>(candidate_r) - accent_r_) * kAccentAdaptRate);
    }

    void interpolate_missing() {
        completed_ = raw_;
        auto first = size_t{0};
        while (first < completed_.size() && std::isnan(completed_[first])) {
            ++first;
        }

        if (first == completed_.size()) {
            std::fill(completed_.begin(), completed_.end(), 0.0f);
            return;
        }

        for (size_t i = 0; i < first; ++i) {
            completed_[i] = completed_[first];
        }

        auto left = first;
        while (left < completed_.size()) {
            auto right = left + 1;
            while (right < completed_.size() && std::isnan(completed_[right])) {
                ++right;
            }

            if (right >= completed_.size()) {
                for (auto i = left + 1; i < completed_.size(); ++i) {
                    completed_[i] = completed_[left];
                }
                break;
            }

            const auto span = right - left;
            for (size_t i = 1; i < span; ++i) {
                const auto t = static_cast<float>(i) / static_cast<float>(span);
                completed_[left + i] = completed_[left]
                    + (completed_[right] - completed_[left]) * t;
            }
            left = right;
        }
    }

    void despike() {
        despiked_ = completed_;
        if (completed_.size() < 3) {
            return;
        }

        for (size_t i = 1; i + 1 < completed_.size(); ++i) {
            const auto left = completed_[i - 1];
            const auto right = completed_[i + 1];
            const auto self = completed_[i];
            if (std::abs(left - right) > kImpulseThresholdPx) {
                continue;
            }

            if (std::abs(self - left) > kImpulseThresholdPx
                && std::abs(self - right) > kImpulseThresholdPx) {
                despiked_[i] = (left + right) * 0.5f;
            }
        }
    }

    void smooth() {
        if (smooth_radius_ <= 0 || despiked_.empty()) {
            smooth_ = despiked_;
            return;
        }

        smooth_.resize(despiked_.size());
        for (size_t i = 0; i < despiked_.size(); ++i) {
            float sum = 0;
            int32_t count = 0;
            const auto begin = i > static_cast<size_t>(smooth_radius_)
                ? i - static_cast<size_t>(smooth_radius_)
                : size_t{0};
            const auto end = std::min(
                despiked_.size() - 1,
                i + static_cast<size_t>(smooth_radius_));
            for (auto j = begin; j <= end; ++j) {
                sum += despiked_[j];
                ++count;
            }
            smooth_[i] = sum / count;
        }
    }

    int32_t inset_left_;
    int32_t inset_top_;
    int32_t inset_right_;
    int32_t inset_bottom_;
    int32_t smooth_radius_;
    uint8_t accent_b_ = 187;
    uint8_t accent_g_ = 125;
    uint8_t accent_r_ = 12;
    bool accent_valid_ = false;
    int32_t accent_fail_streak_ = 0;
    std::vector<float> raw_;
    std::vector<float> completed_;
    std::vector<float> despiked_;
    std::vector<float> smooth_;
};

void fill_frame_info(
    CaptureFrameInfo& info,
    uint64_t sequence,
    const CaptureConfig& config,
    int32_t plot_width,
    uint8_t accent_b,
    uint8_t accent_g,
    uint8_t accent_r,
    bool has_timing,
    int64_t last_update,
    int64_t update_period) noexcept {
    info = {};
    info.sequence = sequence;
    info.frame_width = config.width;
    info.frame_height = config.height;
    info.inset_left = config.inset_left;
    info.inset_top = config.inset_top;
    info.inset_right = config.inset_right;
    info.inset_bottom = config.inset_bottom;
    info.plot_width = plot_width;
    info.accent_b = accent_b;
    info.accent_g = accent_g;
    info.accent_r = accent_r;
    info.has_update_timing = has_timing ? 1 : 0;
    info.last_update_ticks = last_update;
    info.update_period_ticks = update_period;
    info.next_update_ticks = last_update + update_period;
}

bool valid_config(const CaptureConfig& config) noexcept {
    if (config.main_hwnd == 0 || config.width < 16 || config.height < 16) {
        return false;
    }
    if (config.inset_left < 0 || config.inset_top < 0
        || config.inset_right < 0 || config.inset_bottom < 0) {
        return false;
    }
    return config.width - config.inset_left - config.inset_right >= 8
        && config.height - config.inset_top - config.inset_bottom >= 8;
}

IDirect3DDevice create_direct3d_device(
    winrt::com_ptr<ID3D11Device>& device,
    winrt::com_ptr<ID3D11DeviceContext>& context) {
    constexpr auto flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    auto result = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        flags,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        device.put(),
        nullptr,
        context.put());
    if (FAILED(result)) {
        winrt::check_hresult(D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            flags,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            device.put(),
            nullptr,
            context.put()));
    }

    auto dxgi_device = device.as<IDXGIDevice>();
    winrt::com_ptr<IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(
        dxgi_device.get(),
        inspectable.put()));
    return inspectable.as<IDirect3DDevice>();
}

GraphicsCaptureItem create_capture_item(HWND hwnd) {
    auto interop = winrt::get_activation_factory<
        GraphicsCaptureItem,
        IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr};
    winrt::check_hresult(interop->CreateForWindow(
        hwnd,
        winrt::guid_of<GraphicsCaptureItem>(),
        winrt::put_abi(item)));
    return item;
}

class NativeCaptureSession final
    : public std::enable_shared_from_this<NativeCaptureSession> {
public:
    static std::shared_ptr<NativeCaptureSession> create(const CaptureConfig& config) {
        auto session = std::shared_ptr<NativeCaptureSession>(new NativeCaptureSession(config));
        session->start();
        return session;
    }

    ~NativeCaptureSession() {
        stop();
    }

    void stop() noexcept {
        if (stopping_.exchange(true)) {
            return;
        }

        std::scoped_lock frame_lock(frame_mutex_);
        try {
            if (frame_pool_ && subscribed_) {
                frame_pool_.FrameArrived(frame_arrived_token_);
                subscribed_ = false;
            }
            if (capture_session_) {
                capture_session_.Close();
            }
            if (frame_pool_) {
                frame_pool_.Close();
            }
        } catch (...) {
        }
        capture_session_ = nullptr;
        frame_pool_ = nullptr;
        item_ = nullptr;
        staging_ = nullptr;
        context_ = nullptr;
        device_ = nullptr;
    }

    int32_t try_get(
        uint64_t after_sequence,
        float* output,
        int32_t capacity,
        CaptureFrameInfo* out_info) const noexcept {
        if (output == nullptr || out_info == nullptr || capacity < 0) {
            return E_INVALIDARG;
        }

        const auto error = last_error_.load();
        if (FAILED(error)) {
            return error;
        }

        std::scoped_lock lock(state_mutex_);
        if (sequence_ == 0 || sequence_ <= after_sequence) {
            return 0;
        }
        if (capacity < static_cast<int32_t>(latest_y_.size())) {
            return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        }

        std::copy(latest_y_.begin(), latest_y_.end(), output);
        fill_frame_info(
            *out_info,
            sequence_,
            config_,
            static_cast<int32_t>(latest_y_.size()),
            latest_accent_b_,
            latest_accent_g_,
            latest_accent_r_,
            update_intervals_.size() >= 3,
            last_update_ticks_,
            update_period_ticks_);
        return 1;
    }

private:
    explicit NativeCaptureSession(const CaptureConfig& config)
        : config_(config),
          extractor_(
              config.inset_left,
              config.inset_top,
              config.inset_right,
              config.inset_bottom,
              config.smooth_radius) {}

    void start() {
        if (!valid_config(config_)) {
            winrt::throw_hresult(E_INVALIDARG);
        }
        const auto hwnd = reinterpret_cast<HWND>(
            static_cast<intptr_t>(config_.main_hwnd));
        if (!IsWindow(hwnd) || !GraphicsCaptureSession::IsSupported()) {
            winrt::throw_hresult(E_NOTIMPL);
        }

        direct3d_device_ = create_direct3d_device(device_, context_);
        item_ = create_capture_item(hwnd);
        const auto size = item_.Size();
        if (size.Width < 1 || size.Height < 1) {
            winrt::throw_hresult(E_INVALIDARG);
        }

        frame_pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
            direct3d_device_,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            size);
        capture_session_ = frame_pool_.CreateCaptureSession(item_);
        capture_session_.IsCursorCaptureEnabled(false);

        const std::weak_ptr<NativeCaptureSession> weak = shared_from_this();
        frame_arrived_token_ = frame_pool_.FrameArrived(
            [weak](const Direct3D11CaptureFramePool& sender, const auto&) {
                if (const auto self = weak.lock()) {
                    self->on_frame_arrived(sender);
                }
            });
        subscribed_ = true;
        capture_session_.StartCapture();
    }

    void on_frame_arrived(const Direct3D11CaptureFramePool& sender) noexcept {
        if (stopping_.load()) {
            return;
        }

        std::scoped_lock frame_lock(frame_mutex_);
        if (stopping_.load()) {
            return;
        }

        try {
            auto frame = sender.TryGetNextFrame();
            if (!frame) {
                return;
            }

            auto surface_access = frame.Surface().as<
                ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            winrt::com_ptr<ID3D11Texture2D> texture;
            winrt::check_hresult(surface_access->GetInterface(
                __uuidof(ID3D11Texture2D),
                texture.put_void()));
            process_texture(texture.get(), frame.SystemRelativeTime().count());
        } catch (const winrt::hresult_error& error) {
            last_error_.store(error.code().value);
        } catch (...) {
            last_error_.store(E_FAIL);
        }
    }

    void process_texture(ID3D11Texture2D* texture, int64_t present_ticks) {
        D3D11_TEXTURE2D_DESC source_desc{};
        texture->GetDesc(&source_desc);

        RECT outer{};
        const auto hwnd = reinterpret_cast<HWND>(
            static_cast<intptr_t>(config_.main_hwnd));
        if (FAILED(DwmGetWindowAttribute(
                hwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                &outer,
                sizeof(outer)))) {
            return;
        }
        const auto outer_width = outer.right - outer.left;
        const auto outer_height = outer.bottom - outer.top;
        if (outer_width <= 0 || outer_height <= 0) {
            winrt::throw_hresult(E_INVALIDARG);
        }

        const auto scale_x = static_cast<double>(source_desc.Width) / outer_width;
        const auto scale_y = static_cast<double>(source_desc.Height) / outer_height;
        const auto source_x = static_cast<int32_t>(std::nearbyint(
            (config_.screen_left - outer.left) * scale_x));
        const auto source_y = static_cast<int32_t>(std::nearbyint(
            (config_.screen_top - outer.top) * scale_y));
        const auto source_width = std::max(1, static_cast<int32_t>(std::nearbyint(
            config_.width * scale_x)));
        const auto source_height = std::max(1, static_cast<int32_t>(std::nearbyint(
            config_.height * scale_y)));
        if (source_x < 0 || source_y < 0
            || source_x + source_width > static_cast<int32_t>(source_desc.Width)
            || source_y + source_height > static_cast<int32_t>(source_desc.Height)) {
            return;
        }

        ensure_staging_texture(source_width, source_height, source_desc.Format);
        const D3D11_BOX source_box{
            static_cast<UINT>(source_x),
            static_cast<UINT>(source_y),
            0,
            static_cast<UINT>(source_x + source_width),
            static_cast<UINT>(source_y + source_height),
            1};
        context_->CopySubresourceRegion(
            staging_.get(),
            0,
            0,
            0,
            0,
            texture,
            0,
            &source_box);

        D3D11_MAPPED_SUBRESOURCE mapped{};
        winrt::check_hresult(context_->Map(
            staging_.get(),
            0,
            D3D11_MAP_READ,
            0,
            &mapped));
        struct UnmapGuard {
            ID3D11DeviceContext* context;
            ID3D11Texture2D* texture;
            ~UnmapGuard() { context->Unmap(texture, 0); }
        } unmap{context_.get(), staging_.get()};

        const BgraView view{
            static_cast<const uint8_t*>(mapped.pData),
            source_width,
            source_height,
            mapped.RowPitch,
            config_.width,
            config_.height};
        const auto change = sample_change(view);
        if (!change.content_changed) {
            return;
        }

        std::vector<float> height_field;
        uint8_t accent_b = 0;
        uint8_t accent_g = 0;
        uint8_t accent_r = 0;
        if (!extractor_.extract(
                view,
                height_field,
                accent_b,
                accent_g,
                accent_r)) {
            return;
        }

        std::scoped_lock lock(state_mutex_);
        if (change.timing_changed) {
            track_update_timing(present_ticks);
        }
        latest_y_ = std::move(height_field);
        latest_accent_b_ = accent_b;
        latest_accent_g_ = accent_g;
        latest_accent_r_ = accent_r;
        ++sequence_;
    }

    void ensure_staging_texture(
        int32_t width,
        int32_t height,
        DXGI_FORMAT format) {
        if (staging_ && staging_width_ == width && staging_height_ == height
            && staging_format_ == format) {
            return;
        }

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(width);
        desc.Height = static_cast<UINT>(height);
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        winrt::check_hresult(device_->CreateTexture2D(&desc, nullptr, staging_.put()));
        staging_width_ = width;
        staging_height_ = height;
        staging_format_ = format;
    }

    ChangeSample sample_change(const BgraView& view) {
        current_samples_.clear();
        current_samples_.reserve(static_cast<size_t>(
            ((view.output_width + 15) / 16) * ((view.output_height + 15) / 16) * 3));
        for (int32_t y = 0; y < view.output_height; y += 16) {
            for (int32_t x = 0; x < view.output_width; x += 16) {
                const auto pixel = view.at(x, y);
                current_samples_.push_back(pixel.b);
                current_samples_.push_back(pixel.g);
                current_samples_.push_back(pixel.r);
            }
        }

        const auto first_frame = last_samples_.size() != current_samples_.size();
        int64_t difference = 0;
        if (!first_frame) {
            for (size_t i = 0; i < current_samples_.size(); ++i) {
                difference += std::abs(
                    static_cast<int32_t>(last_samples_[i])
                    - static_cast<int32_t>(current_samples_[i]));
            }
        }
        last_samples_.swap(current_samples_);
        return {
            first_frame || difference > 0,
            first_frame || difference > kChangeDiffThreshold};
    }

    void track_update_timing(int64_t present_ticks) {
        if (last_update_ticks_ > 0) {
            const auto interval = present_ticks - last_update_ticks_;
            if (interval > 0 && interval < kMaxUpdateIntervalTicks) {
                update_intervals_.push_back(interval);
                if (update_intervals_.size() > kUpdateIntervalWindow) {
                    update_intervals_.pop_front();
                }
                auto sorted = std::vector<int64_t>(
                    update_intervals_.begin(),
                    update_intervals_.end());
                std::sort(sorted.begin(), sorted.end());
                update_period_ticks_ = sorted[sorted.size() / 2];
            }
        }
        last_update_ticks_ = present_ticks;
    }

    CaptureConfig config_{};
    HeightExtractor extractor_;
    std::atomic<bool> stopping_{false};
    std::atomic<int32_t> last_error_{S_OK};
    mutable std::mutex state_mutex_;
    std::mutex frame_mutex_;
    winrt::com_ptr<ID3D11Device> device_;
    winrt::com_ptr<ID3D11DeviceContext> context_;
    winrt::com_ptr<ID3D11Texture2D> staging_;
    IDirect3DDevice direct3d_device_{nullptr};
    GraphicsCaptureItem item_{nullptr};
    Direct3D11CaptureFramePool frame_pool_{nullptr};
    GraphicsCaptureSession capture_session_{nullptr};
    winrt::event_token frame_arrived_token_{};
    bool subscribed_ = false;
    int32_t staging_width_ = 0;
    int32_t staging_height_ = 0;
    DXGI_FORMAT staging_format_ = DXGI_FORMAT_UNKNOWN;
    std::vector<uint8_t> last_samples_;
    std::vector<uint8_t> current_samples_;
    std::vector<float> latest_y_;
    uint64_t sequence_ = 0;
    uint8_t latest_accent_b_ = 212;
    uint8_t latest_accent_g_ = 120;
    uint8_t latest_accent_r_ = 0;
    std::deque<int64_t> update_intervals_;
    int64_t last_update_ticks_ = 0;
    int64_t update_period_ticks_ = 0;
};

struct ExtractorHandle {
    ExtractorHandle(
        int32_t inset_left,
        int32_t inset_top,
        int32_t inset_right,
        int32_t inset_bottom,
        int32_t smooth_radius)
        : extractor(
              inset_left,
              inset_top,
              inset_right,
              inset_bottom,
              smooth_radius),
          config{
              0,
              0,
              0,
              0,
              0,
              inset_left,
              inset_top,
              inset_right,
              inset_bottom,
              smooth_radius} {}

    std::mutex mutex;
    HeightExtractor extractor;
    CaptureConfig config;
    uint64_t sequence = 0;
};

std::mutex g_capture_mutex;
std::shared_ptr<NativeCaptureSession> g_capture;

}  // namespace

int32_t __stdcall Capture_Start(const CaptureConfig* config) {
    if (config == nullptr || !valid_config(*config)) {
        return E_INVALIDARG;
    }

    try {
        auto next = NativeCaptureSession::create(*config);
        std::shared_ptr<NativeCaptureSession> previous;
        {
            std::scoped_lock lock(g_capture_mutex);
            previous = std::exchange(g_capture, std::move(next));
        }
        if (previous) {
            previous->stop();
        }
        return S_OK;
    } catch (const winrt::hresult_error& error) {
        return error.code().value;
    } catch (...) {
        return E_FAIL;
    }
}

int32_t __stdcall Capture_TryGetHeightField(
    uint64_t after_sequence,
    float* out_y_from_top,
    int32_t capacity,
    CaptureFrameInfo* out_info) {
    std::shared_ptr<NativeCaptureSession> capture;
    {
        std::scoped_lock lock(g_capture_mutex);
        capture = g_capture;
    }
    if (!capture) {
        return E_HANDLE;
    }
    return capture->try_get(after_sequence, out_y_from_top, capacity, out_info);
}

void __stdcall Capture_Stop() {
    std::shared_ptr<NativeCaptureSession> capture;
    {
        std::scoped_lock lock(g_capture_mutex);
        capture = std::exchange(g_capture, nullptr);
    }
    if (capture) {
        capture->stop();
    }
}

void* __stdcall CaptureExtractor_Create(
    int32_t inset_left,
    int32_t inset_top,
    int32_t inset_right,
    int32_t inset_bottom,
    int32_t smooth_radius) {
    if (inset_left < 0 || inset_top < 0 || inset_right < 0 || inset_bottom < 0) {
        return nullptr;
    }
    try {
        return new ExtractorHandle(
            inset_left,
            inset_top,
            inset_right,
            inset_bottom,
            smooth_radius);
    } catch (...) {
        return nullptr;
    }
}

void __stdcall CaptureExtractor_Destroy(void* extractor) {
    delete static_cast<ExtractorHandle*>(extractor);
}

int32_t __stdcall CaptureExtractor_ExtractBgra(
    void* extractor,
    const uint8_t* bgra,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_y_from_top,
    int32_t capacity,
    CaptureFrameInfo* out_info) {
    if (extractor == nullptr || bgra == nullptr || out_y_from_top == nullptr
        || out_info == nullptr || width < 1 || height < 1 || stride < width * 4) {
        return E_INVALIDARG;
    }

    auto* handle = static_cast<ExtractorHandle*>(extractor);
    const auto required = handle->extractor.plot_width(width);
    if (capacity < required) {
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }

    try {
        std::scoped_lock lock(handle->mutex);
        handle->config.width = width;
        handle->config.height = height;
        const BgraView view{
            bgra,
            width,
            height,
            static_cast<uint32_t>(stride),
            width,
            height};
        std::vector<float> result;
        uint8_t accent_b = 0;
        uint8_t accent_g = 0;
        uint8_t accent_r = 0;
        if (!handle->extractor.extract(
                view,
                result,
                accent_b,
                accent_g,
                accent_r)) {
            return 0;
        }

        std::copy(result.begin(), result.end(), out_y_from_top);
        fill_frame_info(
            *out_info,
            ++handle->sequence,
            handle->config,
            static_cast<int32_t>(result.size()),
            accent_b,
            accent_g,
            accent_r,
            false,
            0,
            0);
        return 1;
    } catch (...) {
        return E_FAIL;
    }
}
