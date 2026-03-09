#if GODOT
using System;
using Godot;
using AECC.GameEngineAPI;

public partial class GodotRootNode: EngineApiObjectBehaviour
{
    public static Godot.Node globalRoot = null;
}
#endif
