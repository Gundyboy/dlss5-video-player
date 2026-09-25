#include "NeuralWorker.h"
#include "PlatformPaths.h"
#include "ReShadeConfig.h"
#include "Utf8Text.h"

#include <windows.h>

#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <cwchar>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;

int Fail(const std::string& message)
{
    std::cerr << message << '\n';
    return 1;
}

bool ParseSide(const wchar_t* text, uint32_t& side)
{
    if (!text || !*text) return false;
    wchar_t* end = nullptr;
    errno = 0;
    const unsigned long value = std::wcstoul(text, &end, 10);
    if (errno || *end || value < 64 || value > 8192) return false;
    side = static_cast<uint32_t>(value);
    return true;
}

} // namespace

int wmain(int argc, wchar_t* argv[])
{
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (argc != 9 || std::wstring(argv[1]) != L"--input" ||
        std::wstring(argv[3]) != L"--output" ||
        std::wstring(argv[5]) != L"--width" ||
        std::wstring(argv[7]) != L"--height")
        return Fail("Usage: DLSSPhotoshopNeural --input <bmp> --output <mkv> --width <pixels> --height <pixels>");

    uint32_t width = 0;
    uint32_t height = 0;
    if (!ParseSide(argv[6], width) || !ParseSide(argv[8], height) ||
        static_cast<uint64_t>(width) * height > 64'000'000)
        return Fail("Image dimensions must be 64-8192 pixels per side and at most 64 megapixels.");

    const fs::path input(argv[2]);
    const fs::path output(argv[4]);
    std::error_code error;
    if (!fs::is_regular_file(input, error) || error)
        return Fail("The input BMP does not exist.");
    if (!fs::is_directory(output.parent_path(), error) || error)
        return Fail("The output directory does not exist.");
    if (fs::equivalent(input, output, error) && !error)
        return Fail("The output cannot replace the input image.");

    const auto moduleDirectory = platform_paths::ModuleDirectory();
    if (!moduleDirectory) return Fail("Could not locate the neural runner directory.");
    const fs::path runtime = *moduleDirectory / L"neural-runtime";
    const fs::path worker = runtime / L"NeuralWorker.exe";
    if (!fs::is_regular_file(worker, error) || error)
        return Fail("NeuralWorker.exe is missing from neural-runtime.");

    try {
        NeuralRuntimeLease lease(runtime);
        if (!lease.Held())
            return Fail("Another neural render is using this runtime. Try again when it finishes.");

        // Photoshop exposes one control, Mix. The model always runs at the
        // source resolution; the bridge blends its verified frame afterward.
        const std::vector<NeuralAddonOverride> overrides{{"NRPreUpscale", "0"}};
        const ConfigUpdate configured = ConfigureNeuralAddon(runtime / L"ReShade.ini", true, overrides);
        if (!configured.ok)
            return Fail("Could not prepare the neural add-on: " + utf8_text::FromWide(configured.error));

        NeuralRenderRequest request{};
        request.sourcePath = input;
        request.stagingVideoPath = output;
        request.width = width;
        request.height = height;
        request.fps = 1.0;
        request.durationSeconds = 1.0;
        request.requireNeural = true;
        // A resident job reports its verified result before the helper tears
        // down its GPU state. This runner serves one image, then explicitly
        // releases that helper; no key is ever reused across requests.
        const auto key = resident_helper::MakeHelperKey(runtime.wstring(),
            std::to_string(GetCurrentProcessId()), std::to_string(GetTickCount64()));
        ResidentNeuralHelper helper(resident_helper::kDefaultIdleVramPolicy);
        const NeuralRenderResult result = helper.RunJob(worker, key, request, {});
        helper.Release();
        if (!result.ok)
            return Fail("Neural rendering failed: " + utf8_text::FromWide(result.detail));
        if (result.frameCount != 1 || result.verifiedNeuralFrames != 1 ||
            !fs::is_regular_file(output, error) || error)
            return Fail("The neural worker did not produce one verified image frame.");
        std::cout << "Neural frame ready.\n";
        return 0;
    } catch (const std::exception& failure) {
        return Fail(std::string("Neural rendering failed: ") + failure.what());
    }
}
