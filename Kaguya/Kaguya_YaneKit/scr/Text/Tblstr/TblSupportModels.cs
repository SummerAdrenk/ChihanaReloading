namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblSupportDocument
{
    public string Format { get; set; } = "TBL";
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public TblLineTable? LineTable { get; set; }
    public TblLabelTable? LabelTable { get; set; }
    public TblEventFgTable? EventFgTable { get; set; }
}

public sealed class TblLineTable
{
    public byte XorMask { get; set; } = 0xFF;
    public List<TblLineEntry> Entries { get; set; } = [];
}

public sealed class TblLineEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}

public sealed class TblLabelTable
{
    public List<TblLabelEntry> Entries { get; set; } = [];
}

public sealed class TblLabelEntry
{
    public int Index { get; set; }
    public string ScriptFile { get; set; } = "";
    public string Label { get; set; } = "";
    public int TargetOffset { get; set; }
}

public sealed class TblEventFgTable
{
    public byte XorKey { get; set; }
    public List<TblEventFgCharacter> Characters { get; set; } = [];
    public List<TblEventFgKaisou> Kaisou { get; set; } = [];
}

public sealed class TblEventFgCharacter
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public List<TblEventFgSlot> Slots { get; set; } = [];
}

public sealed class TblEventFgSlot
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public List<TblEventFgEvent> Events { get; set; } = [];
}

public sealed class TblEventFgEvent
{
    public int Index { get; set; }
    public int Field0 { get; set; }
    public int Field1 { get; set; }
    public string Name { get; set; } = "";
}

public sealed class TblEventFgKaisou
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public List<TblEventFgKaiSlot> Slots { get; set; } = [];
}

public sealed class TblEventFgKaiSlot
{
    public int Index { get; set; }
    public int Field0 { get; set; }
    public string SlotName { get; set; } = "";
    public string ScriptName { get; set; } = "";
}
