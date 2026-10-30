#include "BootstrapClient.h"
#include "DiscoveryWatcher.h"
#include <iostream>
#include <cmath>

using namespace workfor::native;

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 3 && wcscmp(argv[1], L"--probe-target") == 0)
            return RunProbeTarget(argv[2]);
        if (argc == 2 && wcscmp(argv[1], L"--stdio") == 0)
            return RunControlStdio();
        if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0) {
            const auto image = std::filesystem::path(ProcessImagePath(GetCurrentProcess()));
            std::cout << RunBootstrapExperiment(image).dump(2) << std::endl;
            return 0;
        }
        if (argc == 2 && wcscmp(argv[1], L"--probe-explorer") == 0) {
            const auto image = std::filesystem::path(ProcessImagePath(GetCurrentProcess()));
            const auto result = RunExplorerInspection(image);
            std::cout << result.dump(2) << std::endl;
            return result.at("result").at("inspection").value("result", -1L) < 0 ? 1 : 0;
        }
        if ((argc == 2 || argc == 4) && wcscmp(argv[1], L"--run-explorer") == 0) {
            double width = 260;
            if (argc == 4) {
                if (wcscmp(argv[2], L"--width") != 0) throw std::invalid_argument("Expected --width");
                wchar_t* end{}; width = wcstod(argv[3], &end);
                if (!end || *end || !std::isfinite(width) || width < 180 || width > 360)
                    throw std::invalid_argument("--width requires 180..360 DIP");
            }
            const auto image = std::filesystem::path(ProcessImagePath(GetCurrentProcess()));
            const auto report = RunExplorerContinuous(image, width);
            std::cout << nlohmann::json{{"type", "bridge.liveStopped"},
                {"layoutAttached", false}, {"result", report.at("result").at("detach")}}.dump() << std::endl;
            return 0;
        }
        if ((argc == 4 || argc == 6) && wcscmp(argv[1], L"--attach-explorer") == 0 &&
            wcscmp(argv[2], L"--duration") == 0) {
            wchar_t* end{};
            const auto seconds = wcstoul(argv[3], &end, 10);
            if (!end || *end || seconds == 0 || seconds > 300)
                throw std::invalid_argument("--duration requires 1..300 seconds");
            double width = 260;
            if (argc == 6) {
                if (wcscmp(argv[4], L"--width") != 0) throw std::invalid_argument("Expected --width");
                width = wcstod(argv[5], &end);
                if (!end || *end || !std::isfinite(width) || width < 180 || width > 360)
                    throw std::invalid_argument("--width requires 180..360 DIP");
            }
            const auto image = std::filesystem::path(ProcessImagePath(GetCurrentProcess()));
            std::cout << RunExplorerLiveExperiment(image, static_cast<unsigned>(seconds), width).dump(2) << std::endl;
            return 0;
        }
        if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--inspect") == 0)) {
            nlohmann::json targets = nlohmann::json::array();
            for (const auto& target : DiscoverTaskbars()) {
                auto item = Describe(target);
                try { ValidateExplorerTarget(target); item["sameUserTargetValid"] = true; }
                catch (const std::exception& error) {
                    item["sameUserTargetValid"] = false; item["error"] = error.what();
                }
                item["bootstrapAllowed"] = false;
                item["reason"] = "Production compatibility adapter has not been verified";
                targets.push_back(std::move(item));
            }
            std::cout << nlohmann::json{{"type", "bridge.discovery"},
                {"nativeAbiVersion", kNativeAbiVersion}, {"targets", targets},
                {"readOnly", true}}.dump(2) << std::endl;
            return 0;
        }
        std::cerr << "Usage: WorkFor.NativeBridge.exe [--inspect | --self-test | --stdio | --probe-explorer | --run-explorer]\n"
            "       WorkFor.NativeBridge.exe --attach-explorer --duration 1..300\n"
            "Live research modes are exact-fingerprint gated and use only WorkFor.Taskbar.Live.dll.\n";
        return 2;
    } catch (const std::exception& error) {
        const auto code = ClassifyNativeFailure(error);
        if (argc >= 2 && wcscmp(argv[1], L"--run-explorer") == 0)
            std::cout << nlohmann::json{{"type", "bridge.liveState"}, {"payload", {
                {"state", code == "DETACH_PENDING" ? "DetachPending" : "NativeError"},
                {"code", code}, {"layoutAttached", code == "DETACH_PENDING"}}}}.dump() << std::endl;
        std::cerr << nlohmann::json{{"type", "bridge.error"}, {"code", code},
            {"message", error.what()}, {"layoutAttached", false}}.dump() << std::endl;
        return 1;
    }
}
