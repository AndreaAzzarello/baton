using System.Runtime.CompilerServices;
using Baton.Core.Platform;

namespace Baton.UiTests;

/// <summary>Runs before any test: as in the core tests, touching the real registry throws and fails the test.</summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void ForbidRealRegistry() => WindowsUserRegistry.Forbidden = true;
}
