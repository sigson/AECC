using System.Runtime.CompilerServices;

// Characterization tests reach into internal seams (ActivateSquashRedirect, TypeRegistry.Register).
// These attributes are declared here rather than via a csproj item because
// GenerateAssemblyInfo=false disables attribute generation from the project.
[assembly: InternalsVisibleTo("AECC.Serialization")]
[assembly: InternalsVisibleTo("AECC.Query")]
[assembly: InternalsVisibleTo("AECC.Tests.Phase0.Net")]
[assembly: InternalsVisibleTo("AECC.Tests.Phase2")]
