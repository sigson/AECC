using System.Runtime.CompilerServices;

// Характеризационная сетка дергает internal-швы (ActivateSquashRedirect, TypeRegistry.Register).
// Атрибуты заданы файлом, а не csproj-item'ом: GenerateAssemblyInfo=false отключает
// генерацию атрибутов из проекта (причина CS1061 в первой сборке тестов).
[assembly: InternalsVisibleTo("AECC.Serialization")]
[assembly: InternalsVisibleTo("AECC.Query")]
[assembly: InternalsVisibleTo("AECC.Tests.Phase0.Net")]
[assembly: InternalsVisibleTo("AECC.Tests.Phase2")]
