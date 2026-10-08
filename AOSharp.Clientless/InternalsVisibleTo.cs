// Additive, behaviour-neutral: lets the offline BuffDryRun tool build a simulated LocalPlayer
// through the internal SetStat. Nothing else in the SDK is affected. (GenerateAssemblyInfo is
// off in Directory.Build.props, so the attribute is declared in source here rather than via the
// csproj InternalsVisibleTo item.)
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BuffDryRun")]
