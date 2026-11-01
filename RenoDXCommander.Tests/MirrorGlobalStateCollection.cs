using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// <see cref="Services.Net.MirrorRuntime"/> and the integrity-skip reason are process-wide statics.
/// Test classes that reset or assert them must not run in parallel with each other, or one class's
/// Reset() lands in the middle of another class's assertion.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class MirrorGlobalStateCollection
{
    public const string Name = "Mirror global state";
}
