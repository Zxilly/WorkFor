#include "Adapter.h"
#include "AdapterXaml.h"
#include "Compatibility.h"

#include <cmath>
#include <functional>
#include <filesystem>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Automation.Peers.h>
#include <winrt/Windows.UI.Xaml.Automation.Provider.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Data.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>

using namespace winrt::Windows::UI::Xaml;
using namespace winrt::Windows::UI::Xaml::Controls;
namespace adapter = workfor::taskbar;

void Check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

void Layout(const Grid& root, double width, double height = 48) {
    root.Measure({static_cast<float>(width), static_cast<float>(height)});
    root.Arrange({0, 0, static_cast<float>(width), static_cast<float>(height)});
    root.UpdateLayout();
}

struct Fixture {
    Grid root;
    Grid frame;
    Grid repeater;
    Grid tray;
    Fixture() {
        frame.Name(L"TaskbarFrame");
        repeater.Name(L"TaskbarFrameRepeater");
        tray.Name(L"SystemTrayFrame");
        tray.Width(120);
        tray.HorizontalAlignment(HorizontalAlignment::Right);
        frame.Children().Append(repeater);
        root.Children().Append(frame);
        root.Children().Append(tray);
        Layout(root, 800);
    }
    ~Fixture() {
        std::wstring ignored;
        adapter::AdapterDetach(ignored);
        adapter::AdapterSetActivationHandler(nullptr, nullptr);
    }
    void Attach(double width = 260, HWND window = nullptr) {
        std::wstring diagnostic;
        winrt::check_hresult(adapter::AdapterAttachToRoot(root, width, diagnostic, window));
        Layout(root, 800);
    }
    Button Income() const {
        for (const auto& child : root.Children())
            if (const auto button = child.try_as<Button>(); button && button.Name() == L"WorkForIncomeRegion") return button;
        throw std::runtime_error("native income Button missing");
    }
    void UpdateAndLayout(double width) {
        Layout(root, width);
        winrt::check_hresult(adapter::AdapterUpdate(L"¥128.60", L"实时更新"));
        Layout(root, width);
    }
    void Detach() {
        std::wstring diagnostic;
        winrt::check_hresult(adapter::AdapterDetach(diagnostic));
        Layout(root, 800);
    }
};

class Island {
    HWND window_{};
    winrt::Windows::UI::Xaml::Hosting::DesktopWindowXamlSource source_{nullptr};
public:
    explicit Island(const Grid& root) {
        static constexpr wchar_t name[] = L"WorkFor.Validation.OwnedWindow";
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = name;
        if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            throw std::runtime_error("owned window registration failed");
        window_ = CreateWindowExW(0, name, L"WorkFor owned validation", WS_POPUP,
            100, 100, 1200, 120, nullptr, nullptr, wc.hInstance, nullptr);
        Check(window_ != nullptr, "owned window creation failed");
        source_ = winrt::Windows::UI::Xaml::Hosting::DesktopWindowXamlSource{};
        const auto native = source_.as<IDesktopWindowXamlSourceNative>();
        winrt::check_hresult(native->AttachToWindow(window_));
        HWND child{};
        winrt::check_hresult(native->get_WindowHandle(&child));
        const auto scale = GetDpiForWindow(window_) / 96.0;
        const auto width = static_cast<int>(std::lround(800 * scale));
        const auto height = static_cast<int>(std::lround(48 * scale));
        SetWindowPos(window_, nullptr, 100, 100, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(child, nullptr, 0, 0, width, height, SWP_NOZORDER | SWP_SHOWWINDOW);
        source_.Content(root);
        Layout(root, 800);
    }
    ~Island() {
        source_.Content(nullptr);
        source_.Close();
        DestroyWindow(window_);
    }
    HWND Window() const { return window_; }
};

void DynamicWidths() {
    Fixture f;
    f.Attach();
    std::wstring diagnostic;
    for (const auto width : {180.0, 360.0, 260.0}) {
        winrt::check_hresult(adapter::AdapterConfigure(width, diagnostic));
        f.UpdateAndLayout(800);
        Check(std::abs(f.Income().ActualWidth() - width) < 0.1, "configured native Button width not applied");
        Check(std::abs(f.repeater.ActualWidth() - (800 - 120 - width)) < 0.1,
            "configured income width did not reserve corresponding app space");
    }
    Check(FAILED(adapter::AdapterConfigure(std::numeric_limits<double>::quiet_NaN(), diagnostic)), "NaN width accepted");
    Check(FAILED(adapter::AdapterConfigure(179, diagnostic)), "below-minimum width accepted");
    Check(FAILED(adapter::AdapterConfigure(361, diagnostic)), "above-maximum width accepted");
    f.UpdateAndLayout(800);
    Check(std::abs(f.Income().ActualWidth() - 260) < 0.1, "invalid configuration changed active width");
}

void NarrowRoot() {
    Fixture f;
    f.Attach();
    for (const auto width : {450.0, 300.0, 200.0}) {
        f.UpdateAndLayout(width);
        Check(f.Income().Visibility() == Visibility::Collapsed, "income is still visible when taskbar has insufficient space");
        Check(std::abs(f.root.ColumnDefinitions().GetAt(1).ActualWidth()) < 0.1, "hidden income still occupies layout width");
        Check(std::abs(f.repeater.ActualWidth() - std::max(0.0, width - 120)) < 0.1,
            "collapsed income did not release its app space");
    }
    f.UpdateAndLayout(800);
    Check(f.Income().Visibility() == Visibility::Visible, "income did not restore after space returned");
    Check(std::abs(f.repeater.ActualWidth() - 420) < 0.1, "restored income has wrong app width");
}

void RestoreLocalValues() {
    Fixture f;
    Check(f.frame.ReadLocalValue(Grid::ColumnSpanProperty()) == DependencyProperty::UnsetValue(), "fixture span must start unset");
    Check(f.tray.ReadLocalValue(Grid::ColumnProperty()) == DependencyProperty::UnsetValue(), "fixture column must start unset");
    f.Attach();
    f.Detach();
    Check(f.root.ColumnDefinitions().Size() == 0 && f.root.Children().Size() == 2, "original root structure not restored");
    Check(f.frame.ReadLocalValue(Grid::ColumnSpanProperty()) == DependencyProperty::UnsetValue(), "frame local span was not restored to unset");
    Check(f.tray.ReadLocalValue(Grid::ColumnProperty()) == DependencyProperty::UnsetValue(), "tray local column was not restored to unset");
    Check(f.repeater.ReadLocalValue(FrameworkElement::WidthProperty()) == DependencyProperty::UnsetValue(), "repeater width local value not restored");
    Check(f.repeater.ReadLocalValue(FrameworkElement::HorizontalAlignmentProperty()) == DependencyProperty::UnsetValue(), "repeater alignment local value not restored");
}

void ExistingBinding() {
    Fixture f;
    Island island(f.root);
    Button source;
    source.Tag(winrt::box_value(1));
    winrt::Windows::UI::Xaml::Data::Binding binding;
    binding.Source(source);
    binding.Path(PropertyPath(L"Tag"));
    f.frame.SetBinding(Grid::ColumnSpanProperty(), binding);
    std::cout << "{\"bindingPhase\":\"fixture-applied\"}\n";
    Layout(f.root, 800);
    Check(Grid::GetColumnSpan(f.frame) == 1, "fixture binding did not establish initial span");
    Check(static_cast<bool>(f.frame.GetBindingExpression(Grid::ColumnSpanProperty())), "fixture has no binding expression");
    std::wstring diagnostic;
    const auto attach = adapter::AdapterAttachToRoot(f.root, 260, diagnostic, island.Window());
    std::cout << "{\"bindingPhase\":\"attach-returned\",\"hresult\":" << attach << "}\n";
    if (FAILED(attach)) {
        Check(f.root.ColumnDefinitions().Size() == 0, "refused bound layout still created columns");
    } else {
        f.Detach();
    }
    Check(static_cast<bool>(f.frame.GetBindingExpression(Grid::ColumnSpanProperty())),
        "existing taskbar binding was destroyed by attach/detach");
    source.Tag(winrt::box_value(2));
    Layout(f.root, 800);
    Check(Grid::GetColumnSpan(f.frame) == 2, "restored binding no longer responds to its source");
}

void UnknownLayout() {
    Grid root;
    root.Name(L"UnexpectedRoot");
    root.Children().Append(TextBlock{});
    Layout(root, 800);
    std::wstring diagnostic;
    Check(FAILED(adapter::AdapterAttachToRoot(root, 260, diagnostic)), "unknown root was modified");
    Check(root.ColumnDefinitions().Size() == 0 && root.Children().Size() == 1, "failed attach changed unknown root");
}

void WrongThread() {
    Fixture f;
    f.Attach();
    HRESULT result{};
    std::thread worker([&] { result = adapter::AdapterUpdate(L"¥1.00", L"test"); });
    worker.join();
    Check(result == RPC_E_WRONG_THREAD, "cross-thread XAML mutation was not rejected");
}

void UnknownFingerprint() {
    Check(GetModuleHandleW(L"Taskbar.dll") == nullptr, "owned validation process already loaded a Taskbar module");
    wchar_t path[32768]{};
    Check(GetModuleFileNameW(nullptr, path, ARRAYSIZE(path)) != 0, "cannot identify validation executable");
    const auto fakePath = std::filesystem::path(path).parent_path() / L"Taskbar.dll";
    const auto module = LoadLibraryExW(fakePath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    Check(module != nullptr, "owned fake Taskbar module failed to load");
    std::wstring diagnostic;
    const auto result = adapter::PrepareTaskbarCompatibility(diagnostic);
    FreeLibrary(module);
    Check(result == HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH), "unknown Taskbar module fingerprint was not refused");
    Check(diagnostic.starts_with(L"UNSUPPORTED_BUILD"), "unknown fingerprint has no explicit unsupported-build diagnostic");
}

winrt::Windows::Foundation::Rect Bounds(const FrameworkElement& element, const FrameworkElement& relative) {
    const auto p = element.TransformToVisual(relative).TransformPoint({0, 0});
    return {p.X, p.Y, static_cast<float>(element.ActualWidth()), static_cast<float>(element.ActualHeight())};
}

void LongText() {
    Fixture f;
    f.Attach(180);
    winrt::check_hresult(adapter::AdapterUpdate(L"¥999,999,999,999,999,999,999,999.99", L"正在重新连接"));
    Layout(f.root, 800);
    const auto content = f.Income().Content().as<Grid>();
    std::vector<TextBlock> text;
    for (const auto& child : content.Children()) if (auto t = child.try_as<TextBlock>()) text.push_back(t);
    Check(text.size() == 3, "expected title/amount/status native text blocks");
    const auto amount = Bounds(text[1], content);
    const auto status = Bounds(text[2], content);
    const auto overlapX = std::min(amount.X + amount.Width, status.X + status.Width) - std::max(amount.X, status.X);
    const auto overlapY = std::min(amount.Y + amount.Height, status.Y + status.Height) - std::max(amount.Y, status.Y);
    Check(overlapX <= 0.5 || overlapY <= 0.5, "long amount and status overlap visually");
}

void FontEnlargement() {
    Fixture f;
    Island island(f.root);
    f.Attach(180);
    winrt::check_hresult(adapter::AdapterUpdate(L"¥128,888.60", L"实时更新"));
    const auto content = f.Income().Content().as<Grid>();
    std::vector<TextBlock> text;
    for (const auto& child : content.Children()) if (auto t = child.try_as<TextBlock>()) text.push_back(t);
    Check(text.size() == 3, "expected native text controls");
    std::vector<double> original;
    for (const auto& t : text) original.push_back(t.FontSize());
    for (const auto scale : {1.5, 2.0}) {
        for (size_t i = 0; i < text.size(); ++i) text[i].FontSize(original[i] * scale);
        f.UpdateAndLayout(800);
        const auto amount = Bounds(text[1], f.Income());
        std::cout << "{\"fontMultiplier\":" << scale << ",\"amountY\":" << amount.Y
            << ",\"amountHeight\":" << amount.Height << ",\"buttonHeight\":" << f.Income().ActualHeight() << "}\n";
        Check(amount.Y >= -0.5 && amount.Y + amount.Height <= f.Income().ActualHeight() + 0.5,
            "enlarged amount extends beyond the 48-DIP taskbar Button");
    }
    for (size_t i = 0; i < text.size(); ++i) text[i].FontSize(original[i]);
    f.UpdateAndLayout(800);
    Check(text[0].Visibility() == Visibility::Visible && text[2].Visibility() == Visibility::Visible,
        "caption/status did not return after font size returned to normal");
}

void ThemeResources() {
    Fixture f;
    Island island(f.root);
    f.root.RequestedTheme(ElementTheme::Light);
    f.Attach();
    const auto content = f.Income().Content().as<Grid>();
    TextBlock amount{nullptr};
    for (const auto& child : content.Children())
        if (const auto t = child.try_as<TextBlock>(); t && t.Text() == L"--") amount = t;
    Check(static_cast<bool>(amount), "amount TextBlock missing");
    const auto readColor = [&] {
        Layout(f.root, 800);
        MSG message{};
        unsigned count{};
        while (count++ < 100 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Layout(f.root, 800);
        return amount.Foreground().as<winrt::Windows::UI::Xaml::Media::SolidColorBrush>().Color();
    };
    const auto light = readColor();
    f.root.RequestedTheme(ElementTheme::Dark);
    const auto dark = readColor();
    const auto lightSum = static_cast<int>(light.R) + light.G + light.B;
    const auto darkSum = static_cast<int>(dark.R) + dark.G + dark.B;
    std::cout << "{\"lightTextRgbSum\":" << lightSum << ",\"darkTextRgbSum\":" << darkSum << "}\n";
    Check(light.A > 0 && dark.A > 0 && darkSum > lightSum,
        "native amount foreground did not adapt from light to dark theme");
}

void MonthTitle() {
    Fixture f;
    f.Attach();
    winrt::check_hresult(adapter::AdapterUpdate(L"¥16,000.00", L"实时更新", L"本月收入"));
    const auto content = f.Income().Content().as<Grid>();
    TextBlock label{nullptr};
    for (const auto& child : content.Children())
        if (const auto t = child.try_as<TextBlock>(); t && t.Text() == L"本月收入") label = t;
    Check(static_cast<bool>(label), "month snapshot title was not applied to native label");
    auto name = winrt::Windows::UI::Xaml::Automation::AutomationProperties::GetName(f.Income());
    Check(std::wstring_view(name).starts_with(L"本月收入"), "month native accessibility name says today");
    winrt::check_hresult(adapter::AdapterUpdate(L"¥16,000.00", L"正在重新连接"));
    Check(label.Text() == L"本月收入", "connection status update reset month title to today");
    Check(FAILED(adapter::AdapterUpdate(L"¥1.00", L"test", std::wstring(41, L'X'))), "oversized title was accepted");
    Check(label.Text() == L"本月收入", "invalid title changed active label");
}

void Activation() {
    Fixture f;
    Island island(f.root);
    struct Received { unsigned count{}; adapter::Activation last; } received;
    adapter::AdapterSetActivationHandler([](const adapter::Activation& a, void* context) noexcept {
        auto& event = *static_cast<Received*>(context);
        ++event.count;
        event.last = a;
    }, &received);
    f.Attach(260, island.Window());
    auto button = f.Income();
    Check(button.IsTabStop(), "income Button is not keyboard focusable");
    Check(winrt::Windows::UI::Xaml::Automation::AutomationProperties::GetLiveSetting(button) ==
        winrt::Windows::UI::Xaml::Automation::Peers::AutomationLiveSetting::Off,
        "live amount would continually interrupt screen reader");
    using namespace winrt::Windows::UI::Xaml::Automation;
    Peers::ButtonAutomationPeer peer(button);
    const auto invoke = peer.GetPattern(Peers::PatternInterface::Invoke).as<Provider::IInvokeProvider>();
    invoke.Invoke();
    Check(received.count == 1, "native Button activation missing");
    const auto anchor = received.last.screenPhysicalBounds;
    const auto scale = button.XamlRoot().RasterizationScale();
    Check(std::abs((anchor.right - anchor.left) - button.ActualWidth() * scale) <= 1,
        "anchor width is not scaled to screen pixels");
    Check(received.last.dpi == GetDpiForWindow(island.Window()), "activation DPI mismatch");
    std::cout << "{\"physicalDpi\":" << received.last.dpi << ",\"rasterizationScale\":" << scale
        << ",\"anchorWidthPixels\":" << anchor.right - anchor.left << "}\n";
    f.Detach();
    invoke.Invoke();
    Check(received.count == 1, "detached native Button still dispatches activation");
}

int main(int argc, char** argv) {
    const std::string name = argc == 2 ? argv[1] : "";
    try {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        auto xaml = winrt::Windows::UI::Xaml::Hosting::WindowsXamlManager::InitializeForCurrentThread();
        const std::pair<const char*, std::function<void()>> cases[] = {
            {"dynamic-widths", DynamicWidths}, {"narrow-root", NarrowRoot},
            {"restore-local-values", RestoreLocalValues}, {"existing-binding", ExistingBinding}, {"unknown-layout", UnknownLayout},
            {"wrong-thread", WrongThread}, {"unknown-fingerprint", UnknownFingerprint},
            {"long-text", LongText}, {"font-enlargement", FontEnlargement},
            {"theme-resources", ThemeResources}, {"month-title", MonthTitle}, {"activation", Activation}
        };
        bool ran = false;
        for (const auto& test : cases) if (name == test.first) { test.second(); ran = true; }
        Check(ran, "unknown validation case");
        xaml.Close();
        std::cout << "{\"case\":\"" << name << "\",\"status\":\"passed\"}\n";
        return 0;
    } catch (const winrt::hresult_error& e) {
        std::cerr << "{\"case\":\"" << name << "\",\"status\":\"failed\",\"hresult\":" << e.code().value << "}\n";
    } catch (const std::exception& e) {
        std::cerr << "{\"case\":\"" << name << "\",\"status\":\"failed\",\"reason\":\"" << e.what() << "\"}\n";
    }
    return 1;
}
