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
    if (argc < 9 || (argc - 9) % 2 != 0 || std::wstring(argv[1]) != L"--input" ||
        std::wstring(argv[3]) != L"--output" ||
        std::wstring(argv[5]) != L"--width" ||
        std::wstring(argv[7]) != L"--height")
        return Fail("Usage: DLSSPhotoshopNeural --input <bmp> --output <mkv> --width <pixels> --height <pixels> [--output-width <pixels> --output-height <pixels>] [--neural on|off]");

    uint32_t width = 0;
    uint32_t height = 0;
    if (!ParseSide(argv[6], width) || !ParseSide(argv[8], height) ||
        static_cast<uint64_t>(width) * height > 64'000'000)
        return Fail("Image dimensions must be 64-8192 pixels per side and at most 64 megapixels.");

    uint32_t outputWidth = width, outputHeight = height;
    bool requireNeural = true, sawWidth = false, sawHeight = false, sawNeural = false;
    for (int index = 9; index < argc; index += 2) {
        const std::wstring flag(argv[index]);
        if (flag == L"--output-width" && !sawWidth) {
            sawWidth = true;
            if (!ParseSide(argv[index + 1], outputWidth)) return Fail("Invalid output width.");
        } else if (flag == L"--output-height" && !sawHeight) {
            sawHeight = true;
            if (!ParseSide(argv[index + 1], outputHeight)) return Fail("Invalid output height.");
        } else if (flag == L"--neural" && !sawNeural) {
            sawNeural = true;
            const std::wstring value(argv[index + 1]);
            if (value != L"on" && value != L"off") return Fail("Neural must be on or off.");
            requireNeural = value == L"on";
        } else return Fail("Unknown or duplicate renderer option.");
    }
    const bool upscale = outputWidth > width && outputHeight > height;
    if (sawWidth != sawHeight || outputWidth < width || outputHeight < height ||
        (upscale && (outputWidth % 2 || outputHeight % 2)) ||
        (outputWidth != width || outputHeight != height) && !upscale ||
        static_cast<uint64_t>(outputWidth) * outputHeight > 64'000'000 || (!requireNeural && !upscale))
        return Fail("Output must preserve the source size or upscale both sides to even dimensions within 8192 pixels and 64 megapixels. Upscale-only requires a larger output.");

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

        // Legacy Photoshop requests remain at source size. Batch requests can
        // use the existing Super Resolution carrier before neural rendering.
        const std::vector<NeuralAddonOverride> overrides{{"NRPreUpscale", "0"}};
        const ConfigUpdate configured = ConfigureNeuralAddon(runtime / L"ReShade.ini", requireNeural, overrides);
        if (!configured.ok)
            return Fail("Could not prepare the neural add-on: " + utf8_text::FromWide(configured.error));

        NeuralRenderRequest request{};
        request.sourcePath = input;
        request.stagingVideoPath = output;
        request.width = width;
        request.height = height;
        request.fps = 1.0;
        request.durationSeconds = 1.0;
        request.outputWidth = outputWidth;
        request.outputHeight = outputHeight;
        request.requireNeural = requireNeural;
        if (!requireNeural) request.upscalingHistory = UpscalingHistory::PerFrame;
        // A resident job reports its verified result before the helper tears
        // down its GPU state. This runner serves one image, then explicitly
        // releases that helper; no key is ever reused across requests.
        const auto key = resident_helper::MakeHelperKey(runtime.wstring(),
            std::to_string(GetCurrentProcessId()), std::to_string(GetTickCount64()));
        ResidentNeuralHelper helper(resident_helper::kDefaultIdleVramPolicy);
        // The resident helper is deliberately neural-only. SR-only uses the
        // player's single-shot isolated worker, which disables the add-on.
        const NeuralRenderResult result = requireNeural ? helper.RunJob(worker, key, request, {})
                                                       : RunNeuralWorker(worker, request);
        helper.Release();
        // Keep the configuration stable for both kinds of batch cache entries.
        if (!requireNeural) {
            const auto restored = ConfigureNeuralAddon(runtime / L"ReShade.ini", true, overrides);
            if (!restored.ok) return Fail("Could not restore the neural add-on configuration.");
        }
        if (!result.ok)
            return Fail("Neural rendering failed: " + utf8_text::FromWide(result.detail));
        if (result.frameCount != 1 || result.neural != requireNeural ||
            (requireNeural ? result.verifiedNeuralFrames != 1 : result.verifiedNeuralFrames != 0 || result.nativeEvaluations < 1) ||
            !fs::is_regular_file(output, error) || error)
            return Fail("The worker did not produce one verified image frame in the requested mode.");
        std::cout << (requireNeural ? "Neural frame ready.\n" : "DLSS Super Resolution frame ready.\n");
        return 0;
    } catch (const std::exception& failure) {
        return Fail(std::string("Neural rendering failed: ") + failure.what());
    }
}
