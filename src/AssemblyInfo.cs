using System.Reflection;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("Облачная читалка")]
[assembly: AssemblyDescription("PDF-читалка для учебников, которая помнит страницы")]
[assembly: AssemblyProduct("CutePdfReader")]
// Bump this before every release; release.ps1 tags the GitHub release with it.
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
// csc doesn't add this by itself; without it WPF falls back to .NET 4.0 behaviour
// (for example, no per-monitor DPI scaling).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
