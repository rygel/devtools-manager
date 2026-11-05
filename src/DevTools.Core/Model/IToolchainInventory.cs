using DevTools.Core.Model;

namespace DevTools.Core.Model;

/// <summary>Supplies the set of installed toolchains. Implemented by the providers assembly.</summary>
public interface IToolchainInventory
{
    IReadOnlyList<Toolchain> Discover(CancellationToken cancellationToken = default);
}
