using System.Collections.Generic;
using Dalamud.Configuration;

namespace JumpSolver;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<JumpPuzzle> SavedPuzzles { get; set; } = new();
}
