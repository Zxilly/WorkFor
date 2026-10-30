#include "Adapter.h"
#include "AdapterXaml.h"
#include "Compatibility.h"

#include <algorithm>
#include <cmath>
#include <memory>
#include <sstream>
#include <string_view>
#include <vector>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Data.h>
#include <winrt/Windows.UI.Xaml.Media.h>

namespace workfor::taskbar {
namespace {
using namespace winrt::Windows::UI::Xaml;
using namespace winrt::Windows::UI::Xaml::Controls;
using namespace winrt::Windows::UI::Xaml::Media;

std::string Utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!count) return {};
    std::string result(count, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), count, nullptr, nullptr);
    return result;
}

std::string JsonString(std::wstring_view value) {
    const auto bytes = Utf8(value);
    std::string result = "\"";
    constexpr char hex[] = "0123456789abcdef";
    for (const unsigned char byte : bytes) {
        if (byte == '"' || byte == '\\') { result += '\\'; result += static_cast<char>(byte); }
        else if (byte < 0x20) { result += "\\u00"; result += hex[byte >> 4]; result += hex[byte & 15]; }
        else result += static_cast<char>(byte);
    }
    result += '"';
    return result;
}

struct Node {
    FrameworkElement element{nullptr};
    int parent{-1};
    int depth{};
};

std::vector<Node> Walk(FrameworkElement root) {
    std::vector<Node> result;
    result.push_back({root, -1, 0});
    for (size_t i = 0; i < result.size() && result.size() < 1024; ++i) {
        auto node = result[i];
        if (node.depth >= 20) continue;
        const auto count = VisualTreeHelper::GetChildrenCount(node.element);
        for (int child = 0; child < count && result.size() < 1024; ++child) {
            auto element = VisualTreeHelper::GetChild(node.element, child).try_as<FrameworkElement>();
            if (element) result.push_back({element, static_cast<int>(i), node.depth + 1});
        }
    }
    return result;
}

struct Lease {
    DWORD ownerThread{};
    Grid parent{nullptr};
    ColumnDefinition column{nullptr};
    std::vector<ColumnDefinition> ownedColumns;
    Grid content{nullptr};
    Button button{nullptr};
    winrt::event_token clickToken{};
    HWND taskbarWindow{};
    TextBlock amount{nullptr};
    TextBlock status{nullptr};
    TextBlock label{nullptr};
    TextBlock headingProbe{nullptr};
    struct Change {
        winrt::weak_ref<FrameworkElement> element;
        int before{}; int applied{}; bool span{};
        winrt::Windows::Foundation::IInspectable originalLocal{nullptr};
    };
    std::vector<Change> changes;
    FrameworkElement repeater{nullptr};
    FrameworkElement tray{nullptr};
    double requestedWidth{};
    double originalRepeaterWidth{NAN};
    double appliedRepeaterWidth{NAN};
    HorizontalAlignment originalRepeaterAlignment{};
    winrt::Windows::Foundation::IInspectable repeaterWidthLocal{nullptr};
    winrt::Windows::Foundation::IInspectable repeaterAlignmentLocal{nullptr};
    winrt::event_token layoutToken{};
    bool updating{};
    bool implicitRoot{};
    uint32_t originalChildCount{};
};

// Constructed on first use, not during DllMain. All mutation is on ownerThread.
std::unique_ptr<Lease>& CurrentLease() {
    // Pinned Explorer DLL: no static destructor may release XAML references
    // under the loader lock during process teardown. Explicit detach owns cleanup.
    static auto* lease = new std::unique_ptr<Lease>();
    return *lease;
}

struct ActivationSink { ActivationHandler handler{}; void* context{}; };
ActivationSink& CurrentActivationSink() {
    static ActivationSink sink;
    return sink;
}

void PublishActivation() noexcept {
    try {
        const auto& lease = CurrentLease();
        const auto& sink = CurrentActivationSink();
        if (!lease || !lease->taskbarWindow || !sink.handler) return;
        POINT origin{};
        if (!ClientToScreen(lease->taskbarWindow, &origin)) return;
        const auto root = lease->button.XamlRoot();
        if (!root) return;
        const auto location = lease->button.TransformToVisual(root.Content()).TransformPoint({0, 0});
        const auto scale = root.RasterizationScale();
        Activation activation;
        activation.taskbarWindow = lease->taskbarWindow;
        activation.dpi = GetDpiForWindow(lease->taskbarWindow);
        activation.screenPhysicalBounds = {
            origin.x + static_cast<LONG>(std::lround(location.X * scale)),
            origin.y + static_cast<LONG>(std::lround(location.Y * scale)),
            origin.x + static_cast<LONG>(std::lround((location.X + lease->button.ActualWidth()) * scale)),
            origin.y + static_cast<LONG>(std::lround((location.Y + lease->button.ActualHeight()) * scale))};
        sink.handler(activation, sink.context);
    } catch (...) {}
}

bool HasName(FrameworkElement element, const wchar_t* name) {
    return element && element.Name() == name;
}

void RefreshWidth(Lease& lease) {
    if (lease.updating) return;
    lease.updating = true;
    try {
        const auto total = lease.parent.ActualWidth();
        const auto tray = lease.tray.ActualWidth();
        const auto visible = total >= lease.requestedWidth + tray + 120.0;
        lease.button.Visibility(visible ? Visibility::Visible : Visibility::Collapsed);
        lease.column.Width({visible ? lease.requestedWidth : 0.0, GridUnitType::Pixel});
        if (visible && lease.button.ActualHeight() > 0 && lease.headingProbe) {
            // Measure a nonvisual copy even when the caption is collapsed, so
            // reducing system text size can restore the caption automatically.
            lease.headingProbe.FontSize(lease.label.FontSize());
            lease.headingProbe.FontFamily(lease.label.FontFamily());
            lease.headingProbe.Measure({INFINITY, INFINITY});
            const auto headingHeight = lease.headingProbe.DesiredSize().Height;
            const auto amountHeight = std::max<double>(lease.amount.DesiredSize().Height, lease.amount.ActualHeight());
            const bool captionFits = headingHeight + amountHeight + 2.0 <= lease.button.ActualHeight();
            lease.label.Visibility(captionFits ? Visibility::Visible : Visibility::Collapsed);
            lease.status.Visibility(captionFits ? Visibility::Visible : Visibility::Collapsed);
            lease.amount.Margin({0, captionFits ? 2.0 : 0.0, 0, 0});
        }
        if (lease.repeater && lease.ownedColumns.size() == 3) {
            const auto available = std::max(0.0, total - tray - (visible ? lease.requestedWidth : 0.0));
            if (!std::isfinite(lease.appliedRepeaterWidth) ||
                std::abs(lease.appliedRepeaterWidth - available) > 0.1) {
                lease.repeater.Width(available);
                lease.appliedRepeaterWidth = available;
            }
        }
    } catch (...) {}
    lease.updating = false;
}


} // namespace

HRESULT AdapterConfigure(double widthDip, std::wstring& diagnostic) noexcept {
    try {
        auto& lease = CurrentLease();
        if (!lease) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        if (lease->ownerThread != GetCurrentThreadId()) return RPC_E_WRONG_THREAD;
        if (!std::isfinite(widthDip) || widthDip < 180 || widthDip > 360) {
            diagnostic = L"WIDTH_OUT_OF_RANGE"; return E_INVALIDARG;
        }
        lease->requestedWidth = widthDip;
        lease->button.Width(widthDip);
        lease->content.Width(widthDip);
        lease->status.MaxWidth(std::max(48.0, (widthDip - 24.0) * 0.45));
        RefreshWidth(*lease);
        lease->parent.InvalidateMeasure();
        lease->parent.UpdateLayout();
        diagnostic = lease->button.Visibility() == Visibility::Collapsed ? L"INSUFFICIENT_SPACE" : L"CONFIGURED";
        return S_OK;
    } catch (...) { diagnostic = L"CONFIGURE_FAILED"; return winrt::to_hresult(); }
}


void AdapterSetActivationHandler(ActivationHandler handler, void* context) noexcept {
    CurrentActivationSink() = {handler, context};
}

bool AdapterHasLease() noexcept { return static_cast<bool>(CurrentLease()); }

HRESULT AdapterInspect(HWND taskbarWindow, std::string& json) noexcept {
    try {
        FrameworkElement root{nullptr};
        std::wstring diagnostic;
        const auto hr = ResolveCurrentTaskbarRoot(taskbarWindow, root, diagnostic);
        std::ostringstream stream;
        stream << "{\"result\":" << static_cast<long>(hr)
            << ",\"diagnostic\":" << JsonString(diagnostic)
            << ",\"threadId\":" << GetCurrentThreadId() << ",\"nodes\":[";
        if (SUCCEEDED(hr)) {
            const auto nodes = Walk(root);
            bool comma = false;
            for (size_t i = 0; i < nodes.size(); ++i) {
                const auto& node = nodes[i];
                if (comma) stream << ',';
                comma = true;
                stream << "{\"index\":" << i << ",\"parent\":" << node.parent
                    << ",\"name\":" << JsonString(node.element.Name().c_str())
                    << ",\"type\":" << JsonString(winrt::get_class_name(node.element).c_str())
                    << ",\"width\":" << node.element.ActualWidth()
                    << ",\"height\":" << node.element.ActualHeight()
                    << ",\"column\":" << Grid::GetColumn(node.element)
                    << ",\"span\":" << Grid::GetColumnSpan(node.element);
                const auto point = node.element.TransformToVisual(root).TransformPoint({0, 0});
                stream << ",\"x\":" << point.X << ",\"y\":" << point.Y;
                if (const auto grid = node.element.try_as<Grid>()) {
                    stream << ",\"columns\":" << grid.ColumnDefinitions().Size()
                        << ",\"children\":" << grid.Children().Size();
                }
                stream << '}';
            }
        }
        stream << "]}";
        json = stream.str();
        return hr;
    } catch (...) { return winrt::to_hresult(); }
}

HRESULT AdapterAttach(HWND taskbarWindow, double widthDip, std::wstring& diagnostic) noexcept {
    FrameworkElement root{nullptr};
    const auto hr = ResolveCurrentTaskbarRoot(taskbarWindow, root, diagnostic);
    if (FAILED(hr)) return hr;
    const auto attached = AdapterAttachToRoot(root, widthDip, diagnostic);
    if (SUCCEEDED(attached) && CurrentLease()) CurrentLease()->taskbarWindow = taskbarWindow;
    return attached;
}

HRESULT AdapterAttachToRoot(const FrameworkElement& root, double widthDip, std::wstring& diagnostic, HWND hostWindow) noexcept {
    try {
        if (CurrentLease()) { diagnostic = L"ALREADY_ATTACHED"; return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS); }
        if (!std::isfinite(widthDip) || widthDip < 180 || widthDip > 360) return E_INVALIDARG;
        if (!root || !root.Dispatcher().HasThreadAccess()) return RPC_E_WRONG_THREAD;

        // Both accepted shapes reserve real layout width. The exact Explorer
        // research shape has a top-level implicit Grid, with Frame + Tray siblings.
        Grid rootGrid{nullptr};
        FrameworkElement tray{nullptr};
        FrameworkElement frame{nullptr};
        FrameworkElement repeater{nullptr};
        bool implicitRoot = false;
        if (const auto grid = root.try_as<Grid>(); grid && grid.ColumnDefinitions().Size() == 0 &&
            grid.Children().Size() == 2) {
            for (const auto& child : grid.Children()) {
                const auto element = child.try_as<FrameworkElement>();
                if (!element || Grid::GetColumn(element) != 0 || Grid::GetColumnSpan(element) != 1) continue;
                if (HasName(element, L"TaskbarFrame")) frame = element;
                if (winrt::get_class_name(element) == L"SystemTray.SystemTrayFrame" ||
                    HasName(element, L"SystemTrayFrame")) tray = element;
            }
            if (frame && tray) {
                for (const auto& node : Walk(frame))
                    if (HasName(node.element, L"TaskbarFrameRepeater")) repeater = node.element;
                if (repeater) { rootGrid = grid; implicitRoot = true; }
            }
        }
        for (const auto& node : Walk(root)) {
            if (implicitRoot) break;
            if (HasName(node.element, L"RootGrid")) {
                const auto grid = node.element.try_as<Grid>();
                if (!grid) continue;
                for (const auto& child : grid.Children()) {
                    const auto element = child.try_as<FrameworkElement>();
                    if (element && (HasName(element, L"SystemTrayFrame") ||
                        winrt::get_class_name(element) == L"SystemTray.SystemTrayFrame")) {
                        if (rootGrid) { diagnostic = L"AMBIGUOUS_TASKBAR_ROOT"; return E_FAIL; }
                        rootGrid = grid;
                        tray = element;
                    }
                }
            }
        }
        if (!rootGrid || !tray || (!implicitRoot && rootGrid.ColumnDefinitions().Size() < 2) ||
            Grid::GetColumnSpan(tray) != 1) {
            diagnostic = L"UNSUPPORTED_LAYOUT: no validated direct tray column";
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        const auto slot = implicitRoot ? 1 : Grid::GetColumn(tray);
        if (!implicitRoot && (slot <= 0 || static_cast<uint32_t>(slot) >= rootGrid.ColumnDefinitions().Size())) {
            diagnostic = L"UNSUPPORTED_LAYOUT: tray column out of bounds";
            return E_FAIL;
        }
        for (const auto& child : rootGrid.Children()) {
            const auto element = child.try_as<FrameworkElement>();
            if (!element || element.Name() == L"WorkForIncomeRegion") {
                diagnostic = L"LAYOUT_CONFLICT";
                return E_FAIL;
            }
            const auto column = Grid::GetColumn(element);
            const auto span = Grid::GetColumnSpan(element);
            if (span != 1 || column < 0 || (!implicitRoot && static_cast<uint32_t>(column) >= rootGrid.ColumnDefinitions().Size())) {
                diagnostic = L"UNSUPPORTED_LAYOUT: spanning or invalid root child";
                return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }
        }

        auto lease = std::make_unique<Lease>();
        lease->ownerThread = GetCurrentThreadId();
        lease->taskbarWindow = hostWindow;
        lease->parent = rootGrid;
        lease->implicitRoot = implicitRoot;
        lease->originalChildCount = rootGrid.Children().Size();
        lease->column = ColumnDefinition{};
        lease->column.Width({widthDip, GridUnitType::Pixel});
        lease->content = Grid{};
        lease->content.Name(L"WorkForIncomeContent");
        lease->content.Padding({12, 0, 12, 0});
        lease->content.VerticalAlignment(VerticalAlignment::Center);
        RowDefinition headingRow;
        headingRow.Height({1, GridUnitType::Auto});
        RowDefinition amountRow;
        amountRow.Height({1, GridUnitType::Auto});
        lease->content.RowDefinitions().Append(headingRow);
        lease->content.RowDefinitions().Append(amountRow);
        ColumnDefinition headingColumn;
        headingColumn.Width({1, GridUnitType::Star});
        ColumnDefinition statusColumn;
        statusColumn.Width({1, GridUnitType::Auto});
        lease->content.ColumnDefinitions().Append(headingColumn);
        lease->content.ColumnDefinitions().Append(statusColumn);
        auto label = TextBlock{};
        lease->label = label;
        label.Text(L"今日收入");
        label.FontSize(12);
        label.VerticalAlignment(VerticalAlignment::Top);
        label.TextTrimming(TextTrimming::CharacterEllipsis);
        lease->headingProbe = TextBlock{};
        lease->headingProbe.Text(L"今日收入");
        lease->amount = TextBlock{};
        lease->amount.Text(L"--");
        lease->amount.FontSize(16);
        lease->amount.Margin({0, 2, 0, 0});
        lease->amount.TextTrimming(TextTrimming::CharacterEllipsis);
        lease->amount.TextWrapping(TextWrapping::NoWrap);
        Grid::SetRow(lease->amount, 1);
        Grid::SetColumnSpan(lease->amount, 2);
        lease->status = TextBlock{};
        lease->status.Text(L"连接中");
        lease->status.FontSize(10);
        lease->status.HorizontalAlignment(HorizontalAlignment::Right);
        lease->status.VerticalAlignment(VerticalAlignment::Center);
        lease->status.MaxWidth(std::max(48.0, (widthDip - 24.0) * 0.45));
        lease->status.Margin({6, 0, 0, 0});
        lease->status.TextTrimming(TextTrimming::CharacterEllipsis);
        Grid::SetColumn(lease->status, 1);
        lease->content.Children().Append(label);
        lease->content.Children().Append(lease->amount);
        lease->content.Children().Append(lease->status);
        lease->button = Button{};
        lease->button.Name(L"WorkForIncomeRegion");
        lease->button.Width(widthDip);
        lease->button.Padding({0, 0, 0, 0});
        lease->button.BorderThickness({0, 0, 0, 0});
        lease->button.HorizontalAlignment(HorizontalAlignment::Stretch);
        lease->button.VerticalAlignment(VerticalAlignment::Stretch);
        lease->button.Content(lease->content);
        lease->button.Background(SolidColorBrush{winrt::Windows::UI::Color{0, 0, 0, 0}});
        winrt::Windows::UI::Xaml::Automation::AutomationProperties::SetName(
            lease->button, L"今日收入，正在连接，打开收入详情");
        lease->clickToken = lease->button.Click([](auto&&, auto&&) { PublishActivation(); });
        Grid::SetColumn(lease->button, slot);

        // Prepare the complete reversal before the first mutation.
        lease->tray = tray;
        lease->requestedWidth = widthDip;
        if (implicitRoot) {
            lease->changes.push_back({winrt::make_weak(tray), 0, 2, false});
            lease->changes.push_back({winrt::make_weak(frame), 1, 3, true});
            ColumnDefinition apps;
            apps.Width({1, GridUnitType::Star});
            ColumnDefinition trayColumn;
            trayColumn.Width({1, GridUnitType::Auto});
            lease->ownedColumns = {apps, lease->column, trayColumn};
            lease->repeater = repeater;
            lease->tray = tray;
            lease->requestedWidth = widthDip;
            lease->originalRepeaterWidth = repeater.Width();
            lease->originalRepeaterAlignment = repeater.HorizontalAlignment();
            lease->repeaterWidthLocal = repeater.ReadLocalValue(FrameworkElement::WidthProperty());
            lease->repeaterAlignmentLocal = repeater.ReadLocalValue(FrameworkElement::HorizontalAlignmentProperty());
        } else {
            for (const auto& child : rootGrid.Children()) {
                const auto element = child.as<FrameworkElement>();
                const auto column = Grid::GetColumn(element);
                if (column >= slot) lease->changes.push_back({winrt::make_weak(element), column, column + 1, false});
            }
        }
        for (auto& change : lease->changes) {
            if (const auto element = change.element.get()) {
                const auto property = change.span ? Grid::ColumnSpanProperty() : Grid::ColumnProperty();
                if (element.GetBindingExpression(property)) {
                    diagnostic = L"UNSUPPORTED_LAYOUT: modified grid property has an active binding";
                    return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                }
                change.originalLocal = element.ReadLocalValue(property);
                if (change.originalLocal != DependencyProperty::UnsetValue() &&
                    !change.originalLocal.try_as<winrt::Windows::Foundation::IPropertyValue>()) {
                    diagnostic = L"UNSUPPORTED_LAYOUT: grid property expression cannot be safely restored";
                    return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                }
            }
        }
        if (implicitRoot && (repeater.GetBindingExpression(FrameworkElement::WidthProperty()) ||
            repeater.GetBindingExpression(FrameworkElement::HorizontalAlignmentProperty()) ||
            (lease->repeaterWidthLocal != DependencyProperty::UnsetValue() &&
                !lease->repeaterWidthLocal.try_as<winrt::Windows::Foundation::IPropertyValue>()) ||
            (lease->repeaterAlignmentLocal != DependencyProperty::UnsetValue() &&
                !lease->repeaterAlignmentLocal.try_as<winrt::Windows::Foundation::IPropertyValue>()))) {
            diagnostic = L"UNSUPPORTED_LAYOUT: bound app layout properties are not modified";
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        CurrentLease() = std::move(lease);
        try {
            if (implicitRoot) {
                for (const auto& column : CurrentLease()->ownedColumns) rootGrid.ColumnDefinitions().Append(column);
                repeater.HorizontalAlignment(HorizontalAlignment::Left);
            } else rootGrid.ColumnDefinitions().InsertAt(slot, CurrentLease()->column);
            for (const auto& change : CurrentLease()->changes) {
                if (const auto element = change.element.get()) {
                    if (change.span) Grid::SetColumnSpan(element, change.applied);
                    else Grid::SetColumn(element, change.applied);
                }
            }
            rootGrid.Children().Append(CurrentLease()->button);
            rootGrid.InvalidateMeasure();
            rootGrid.UpdateLayout();
            RefreshWidth(*CurrentLease());
            rootGrid.UpdateLayout();
            CurrentLease()->layoutToken = rootGrid.LayoutUpdated([](auto&&, auto&&) {
                if (CurrentLease()) RefreshWidth(*CurrentLease());
            });
            diagnostic = L"EXPERIMENTAL_COLUMN_ATTACHED; crowding/overflow qualification still pending";
            return S_OK;
        } catch (...) {
            const auto error = winrt::to_hresult();
            std::wstring ignored;
            const auto rollback = AdapterDetach(ignored);
            diagnostic = SUCCEEDED(rollback) ? L"ATTACH_ROLLED_BACK" : L"ATTACH_FAILED_DETACH_PENDING";
            return error;
        }
    } catch (...) { diagnostic = L"ADAPTER_ATTACH_EXCEPTION"; return winrt::to_hresult(); }
}

HRESULT AdapterUpdate(const std::wstring& amount, const std::wstring& status,
    const std::wstring& title) noexcept {
    try {
        auto& lease = CurrentLease();
        if (!lease) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        if (lease->ownerThread != GetCurrentThreadId()) return RPC_E_WRONG_THREAD;
        if (amount.size() > 80 || status.size() > 40 || title.size() > 40) return E_INVALIDARG;
        if (!title.empty()) {
            lease->label.Text(title);
            lease->headingProbe.Text(title);
        }
        lease->amount.Text(amount);
        lease->status.Text(status);
        winrt::Windows::UI::Xaml::Automation::AutomationProperties::SetName(
            lease->button, std::wstring(lease->label.Text().c_str()) + L" " + amount + L"，" + status + L"，打开收入详情");
        RefreshWidth(*lease);
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}

HRESULT AdapterDetach(std::wstring& diagnostic) noexcept {
    try {
        auto& lease = CurrentLease();
        if (!lease) { diagnostic = L"ALREADY_DETACHED"; return S_FALSE; }
        if (lease->ownerThread != GetCurrentThreadId()) return RPC_E_WRONG_THREAD;
        uint32_t columnIndex{};
        if (!lease->parent.ColumnDefinitions().IndexOf(lease->column, columnIndex)) {
            diagnostic = L"DETACH_PENDING: owned column no longer found";
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }
        // If another participant changed a shifted child, do not guess how to
        // restore it or remove its active column. Keep state for a later retry.
        for (const auto& change : lease->changes) {
            if (const auto element = change.element.get()) {
                const auto value = change.span ? Grid::GetColumnSpan(element) : Grid::GetColumn(element);
                if (value != change.applied) {
                    diagnostic = L"DETACH_PENDING: neighboring layout changed";
                    return HRESULT_FROM_WIN32(ERROR_BUSY);
                }
            }
        }
        if (lease->implicitRoot) {
            if (lease->parent.ColumnDefinitions().Size() != 3 ||
                lease->parent.Children().Size() != lease->originalChildCount + 1) {
                diagnostic = L"DETACH_PENDING: root structure changed";
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }
            for (uint32_t i = 0; i < 3; ++i) {
                if (lease->parent.ColumnDefinitions().GetAt(i) != lease->ownedColumns[i]) {
                    diagnostic = L"DETACH_PENDING: root columns changed";
                    return HRESULT_FROM_WIN32(ERROR_BUSY);
                }
            }
            if (lease->repeater.HorizontalAlignment() != HorizontalAlignment::Left ||
                (std::isfinite(lease->appliedRepeaterWidth) &&
                 std::abs(lease->repeater.Width() - lease->appliedRepeaterWidth) > 0.1)) {
                diagnostic = L"DETACH_PENDING: app layout constraint changed";
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }
        }
        if (lease->layoutToken.value) lease->parent.LayoutUpdated(lease->layoutToken);
        uint32_t contentIndex{};
        if (lease->parent.Children().IndexOf(lease->button, contentIndex))
            lease->parent.Children().RemoveAt(contentIndex);
        lease->button.Click(lease->clickToken);
        for (const auto& change : lease->changes) {
            if (const auto element = change.element.get()) {
                const auto property = change.span ? Grid::ColumnSpanProperty() : Grid::ColumnProperty();
                if (change.originalLocal == DependencyProperty::UnsetValue()) element.ClearValue(property);
                else element.SetValue(property, change.originalLocal);
            }
        }
        if (lease->implicitRoot) {
            if (lease->repeaterWidthLocal == DependencyProperty::UnsetValue())
                lease->repeater.ClearValue(FrameworkElement::WidthProperty());
            else lease->repeater.SetValue(FrameworkElement::WidthProperty(), lease->repeaterWidthLocal);
            if (lease->repeaterAlignmentLocal == DependencyProperty::UnsetValue())
                lease->repeater.ClearValue(FrameworkElement::HorizontalAlignmentProperty());
            else lease->repeater.SetValue(FrameworkElement::HorizontalAlignmentProperty(), lease->repeaterAlignmentLocal);
            lease->parent.ColumnDefinitions().Clear();
        } else lease->parent.ColumnDefinitions().RemoveAt(columnIndex);
        lease->parent.InvalidateMeasure();
        lease.reset();
        diagnostic = L"DETACHED";
        return S_OK;
    } catch (...) { diagnostic = L"DETACH_PENDING: exception"; return winrt::to_hresult(); }
}

} // namespace workfor::taskbar
