#include "Adapter.h"
#include "AdapterXaml.h"

#include <cmath>
#include <iostream>
#include <stdexcept>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Automation.Peers.h>
#include <winrt/Windows.UI.Xaml.Automation.Provider.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>

using namespace winrt::Windows::UI::Xaml;
using namespace winrt::Windows::UI::Xaml::Controls;

void Require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

void Layout(const Grid& grid, double width) {
    grid.Measure({static_cast<float>(width), 48});
    grid.Arrange({0, 0, static_cast<float>(width), 48});
    grid.UpdateLayout();
}

int main() {
    try {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        auto xaml = winrt::Windows::UI::Xaml::Hosting::WindowsXamlManager::InitializeForCurrentThread();
        {
            Grid root;
            root.Name(L"RootGrid");
            ColumnDefinition appsColumn;
            appsColumn.Width({1, GridUnitType::Star});
            ColumnDefinition trayColumn;
            trayColumn.Width({1, GridUnitType::Auto});
            root.ColumnDefinitions().Append(appsColumn);
            root.ColumnDefinitions().Append(trayColumn);
            Grid apps;
            apps.Name(L"AppButtons");
            Grid tray;
            tray.Name(L"SystemTrayFrame");
            tray.Width(120);
            Grid::SetColumn(tray, 1);
            root.Children().Append(apps);
            root.Children().Append(tray);

            Layout(root, 800);
            const auto before = apps.ActualWidth();
            std::wstring diagnostic;
            winrt::check_hresult(workfor::taskbar::AdapterAttachToRoot(root, 260, diagnostic));
            Layout(root, 800);
            const auto after = apps.ActualWidth();
            Require(root.ColumnDefinitions().Size() == 3, "owned column missing");
            Require(Grid::GetColumn(tray) == 2, "tray did not move to its new layout column");
            const auto incomeButton = root.Children().GetAt(root.Children().Size() - 1).try_as<Button>();
            Require(incomeButton && incomeButton.IsTabStop(), "income region is not a keyboard-focusable native Button");
            Require(!winrt::Windows::UI::Xaml::Automation::AutomationProperties::GetName(incomeButton).empty(),
                "income region has no accessible name");
            Require(std::abs((before - after) - 260) < 0.01, "income region did not reserve real layout width");
            Require(std::abs(tray.ActualWidth() - 120) < 0.01, "tray width changed");
            winrt::check_hresult(workfor::taskbar::AdapterUpdate(L"¥128.61", L"实时更新"));
            Layout(root, 500);
            Require(std::abs(apps.ActualWidth() - 120) < 0.01, "constrained app region did not shrink with layout");
            winrt::check_hresult(workfor::taskbar::AdapterDetach(diagnostic));
            Layout(root, 800);
            Require(root.ColumnDefinitions().Size() == 2, "owned column not removed");
            Require(root.Children().Size() == 2, "owned income UI not removed");
            Require(Grid::GetColumn(tray) == 1, "tray column not restored");
            Require(std::abs(apps.ActualWidth() - before) < 0.01, "app region width not restored");

            // A third party's live layout change must prevent destructive rollback.
            winrt::check_hresult(workfor::taskbar::AdapterAttachToRoot(root, 260, diagnostic));
            Grid::SetColumn(tray, 1);
            Require(FAILED(workfor::taskbar::AdapterDetach(diagnostic)), "conflicting neighbor was overwritten");
            Grid::SetColumn(tray, 2);
            winrt::check_hresult(workfor::taskbar::AdapterDetach(diagnostic));

            // Match the observed Windows 11 shape: implicit outer Grid has
            // Frame + Tray overlay siblings. Keep full-width Frame/background,
            // reserve columns for income/tray, constrain only the app repeater.
            Grid outer;
            Grid frame;
            frame.Name(L"TaskbarFrame");
            Grid repeater;
            repeater.Name(L"TaskbarFrameRepeater");
            frame.Children().Append(repeater);
            Grid outerTray;
            outerTray.Name(L"SystemTrayFrame");
            outerTray.Width(120);
            outerTray.HorizontalAlignment(HorizontalAlignment::Right);
            outer.Children().Append(frame);
            outer.Children().Append(outerTray);
            Layout(outer, 800);
            winrt::check_hresult(workfor::taskbar::AdapterAttachToRoot(outer, 260, diagnostic));
            Layout(outer, 800);
            Require(outer.ColumnDefinitions().Size() == 3, "implicit root columns missing");
            Require(std::abs(frame.ActualWidth() - 800) < 0.01, "background/frame no longer full width");
            Require(std::abs(repeater.ActualWidth() - 420) < 0.01, "repeater did not reserve income and tray width");
            Layout(outer, 600);
            winrt::check_hresult(workfor::taskbar::AdapterUpdate(L"--", L"连接中"));
            Layout(outer, 600);
            Require(std::abs(repeater.ActualWidth() - 220) < 0.01, "repeater did not respond to layout resize");
            winrt::check_hresult(workfor::taskbar::AdapterDetach(diagnostic));
            Layout(outer, 800);
            Require(outer.ColumnDefinitions().Size() == 0, "implicit columns not restored");
            Require(Grid::GetColumnSpan(frame) == 1 && Grid::GetColumn(outerTray) == 0, "implicit attached properties not restored");
            Require(std::isnan(repeater.Width()), "original implicit width not restored");
            Require(repeater.HorizontalAlignment() == HorizontalAlignment::Stretch, "repeater alignment not restored");

            // Host our own XAML island to exercise the actual system Button
            // Click event and physical screen anchoring, without touching Explorer.
            WNDCLASSW windowClass{};
            windowClass.lpfnWndProc = DefWindowProcW;
            windowClass.hInstance = GetModuleHandleW(nullptr);
            windowClass.lpszClassName = L"WorkFor.AdapterActivationTest";
            Require(RegisterClassW(&windowClass) != 0, "test window class registration failed");
            HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"WorkFor test", WS_POPUP,
                100, 100, 1000, 120, nullptr, nullptr, windowClass.hInstance, nullptr);
            Require(window != nullptr, "test window creation failed");
            winrt::Windows::UI::Xaml::Hosting::DesktopWindowXamlSource source;
            const auto native = source.as<IDesktopWindowXamlSourceNative>();
            winrt::check_hresult(native->AttachToWindow(window));
            HWND island{};
            winrt::check_hresult(native->get_WindowHandle(&island));
            SetWindowPos(island, nullptr, 0, 0, 1000, 120, SWP_NOZORDER | SWP_SHOWWINDOW);
            source.Content(root);
            Layout(root, 800);
            struct ActivationProbe {
                unsigned count{};
                workfor::taskbar::Activation last;
            } activation;
            workfor::taskbar::AdapterSetActivationHandler(
                [](const workfor::taskbar::Activation& event, void* state) noexcept {
                    auto& value = *static_cast<ActivationProbe*>(state);
                    ++value.count;
                    value.last = event;
                }, &activation);
            winrt::check_hresult(workfor::taskbar::AdapterAttachToRoot(root, 260, diagnostic, window));
            Layout(root, 800);
            const auto activeButton = root.Children().GetAt(root.Children().Size() - 1).as<Button>();
            winrt::Windows::UI::Xaml::Automation::Peers::ButtonAutomationPeer peer(activeButton);
            const auto invoke = peer.GetPattern(winrt::Windows::UI::Xaml::Automation::Peers::PatternInterface::Invoke)
                .as<winrt::Windows::UI::Xaml::Automation::Provider::IInvokeProvider>();
            invoke.Invoke();
            Require(activation.count == 1, "system Button invoke did not publish activation");
            Require(activation.last.taskbarWindow == window && activation.last.dpi > 0,
                "activation target or DPI missing");
            Require(activation.last.screenPhysicalBounds.right > activation.last.screenPhysicalBounds.left &&
                activation.last.screenPhysicalBounds.bottom > activation.last.screenPhysicalBounds.top,
                "activation screen anchor is empty");
            winrt::check_hresult(workfor::taskbar::AdapterDetach(diagnostic));
            invoke.Invoke();
            Require(activation.count == 1, "removed Button still publishes activation");
            workfor::taskbar::AdapterSetActivationHandler(nullptr, nullptr);
            source.Content(nullptr);
            source.Close();
            DestroyWindow(window);
            UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
            std::cout << "{\"status\":\"passed\",\"reservedDip\":" << before - after
                << ",\"beforeAppsDip\":" << before << ",\"afterAppsDip\":" << after
                << ",\"activationEvents\":" << activation.count
                << ",\"checks\":[\"real-column\",\"constrained-layout\",\"update\",\"detach\",\"conflict-refusal\",\"implicit-root\",\"full-background\",\"resize\",\"native-button-accessibility\",\"system-button-invoke\",\"physical-anchor\",\"event-revoked\"]}\n";
        }
        xaml.Close();
        return 0;
    } catch (const winrt::hresult_error& error) {
        std::wcerr << L"HRESULT " << std::hex << static_cast<unsigned long>(error.code())
            << L": " << error.message().c_str() << L"\n";
        return 1;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
