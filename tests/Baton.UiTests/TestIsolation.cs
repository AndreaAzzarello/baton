using System.Runtime.CompilerServices;
using Baton.Core.Platform;

namespace Baton.UiTests;

/// <summary>Runs before any test: as in the core tests, touching the real registry throws and fails the test.</summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void ForbidRealRegistry() => WindowsUserRegistry.Forbidden = true;

    /// <summary>Same for the user's own pipe: a test that would message the installed app fails instead.</summary>
    [ModuleInitializer]
    internal static void ForbidRealPipe() => Baton.Core.Integration.ChannelClient.ForbidDefaultPipe = true;
}
