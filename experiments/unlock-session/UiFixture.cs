namespace UnityEngine.UI {
public sealed class Button : UnityEngine.Component {
    public UnityEngine.Events.UnityEvent onClick { get; set; }
    public Button() { onClick = new UnityEngine.Events.UnityEvent(); }
}

public sealed class Image : UnityEngine.Behaviour {
    public object sprite { get; set; }
    public object color { get; set; }
}
}
