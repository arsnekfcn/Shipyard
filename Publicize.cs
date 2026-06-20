// ---------------------------------------------------------------------------------------------------
// Publicizer attribute for the PULSAR (marketplace) build.
//
// Two different build pipelines compile this plugin:
//   * LOCAL build (deploy.sh / MSBuild): Krafs.Publicizer runs as an MSBuild task, publicizes
//     Sandbox.Game, and injects its OWN [IgnoresAccessChecksTo] attribute into an obj/ file. That file
//     is gitignored, so it is NEVER committed.
//   * PULSAR build (from-source on the user's machine): Pulsar does NOT run MSBuild — it feeds only the
//     committed .cs files to Roslyn. So Krafs.Publicizer never runs, and Pulsar would not publicize
//     Sandbox.Game UNLESS it finds the trigger in committed source. Pulsar scans committed source for an
//     attribute whose type name ends in "IgnoresAccessChecksTo" and Cecil-publicizes the named assemblies
//     before compiling. This file supplies exactly that.
//
// It is excluded from the LOCAL build (LOCAL_BUILD is defined in the csproj) because there Krafs already
// supplies an identical attribute type — compiling both would be a duplicate-type error (CS0101).
// Under Pulsar, LOCAL_BUILD is undefined (the csproj is ignored), so this file is the one that counts.
// ---------------------------------------------------------------------------------------------------
#if !LOCAL_BUILD
namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : System.Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }
        public string AssemblyName { get; }
    }
}

// Mirror the csproj's <Publicize Include="Sandbox.Game" />: grant the compiler access to private/internal
// Sandbox.Game members (the F10 blueprint screen's private fields, etc.).
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Sandbox.Game")]
#endif
