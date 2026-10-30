namespace IdleForge.Core;

public enum PanelVisibilityPreference { AlwaysShow, StartHidden, RememberLastState }

public sealed record ScriptPanelBinding(Guid Id, Guid ScriptId, Guid ProfileId, string Selector, string Label,
    bool AutomaticallyDetected, PanelVisibilityPreference Preference, bool LastVisible, DateTimeOffset UpdatedAt);
