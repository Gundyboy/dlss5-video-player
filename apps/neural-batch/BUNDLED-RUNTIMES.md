# Bundled runtime components

The self-contained application includes .NET 10 and Windows Desktop runtime
components. `DOTNET-LICENSE.txt` and `DOTNET-ThirdPartyNotices.txt` accompany
the distribution.

The native neural runner and worker use the Microsoft Visual C++ runtime. The
package includes the Microsoft-signed x64 Visual Studio 2022 redistributable
DLLs from `Microsoft.VC143.CRT` version 14.42.34433. They are installed beside
the native runner. Its worker inherits a process-local PATH containing that
folder, so the CRT remains available without adding unpinned modules to the
verified neural model folder. Setup does not modify the system PATH, install
a system runtime, or require administrator permission for those files.

These Microsoft components are governed by Microsoft's applicable terms.
See [Redistribute Visual C++ files](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)
and the [Visual Studio 2022 distributable code list](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution).
The package preserves the original signed DLLs.

The experimental neural runtime, FFmpeg, and FFprobe are verified/copied from
the user's selected player folder. Their separate terms and provenance are
described in the project's `THIRD_PARTY.md` and copied runtime notices.
