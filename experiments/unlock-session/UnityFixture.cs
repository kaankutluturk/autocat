using System;
using System.Collections.Generic;

namespace UnityEngine {
public class Object {
    public bool Destroyed;
    public static Object Instantiate(Object value) { return (Object)((ICloneable)value).Clone(); }
    public static void Destroy(Object value) { if (value != null)value.Destroyed = true; }

    public static bool operator == (Object a, Object b) {
        bool an = System.Object.ReferenceEquals(a, null) ||
            (!System.Object.ReferenceEquals(a, null) && a.Destroyed);
        bool bn = System.Object.ReferenceEquals(b, null) ||
            (!System.Object.ReferenceEquals(b, null) && b.Destroyed);
        return an || bn ? an == bn : System.Object.ReferenceEquals(a, b);
    }

    public static bool operator != (Object a, Object b) { return !(a == b); }
    public override bool Equals(object obj) { return System.Object.ReferenceEquals(this, obj); }
    public override int GetHashCode() { return base.GetHashCode(); }
}

public class GameObject : Object {
    public bool activeSelf = true, PrefabAsset;
    public bool activeInHierarchy { get { return activeSelf; } }

    public Scene scene {
        get {
            return new Scene {Valid = !PrefabAsset};
        }
    }

    public Transform transform;
    public GameObject() { transform = new Transform(this); }
    public void SetActive(bool active) { activeSelf = active; }
}

public struct Scene {
    public bool Valid;
    public bool IsValid() { return Valid; }
}

public class Transform : System.Collections.IEnumerable {
    public GameObject gameObject;
    public Transform parent;
    public List<Transform> Children = new List<Transform>();
    public Vector3 localPosition;
    public Transform(GameObject go) { gameObject = go; }

    public void SetParent(Transform target, bool world) {
        if (parent != null)parent.Children.Remove(this);
        parent = target;
        if (target != null)target.Children.Add(this);
    }

    public System.Collections.IEnumerator GetEnumerator() { return Children.GetEnumerator(); }
}

public class Component : Object {
    public Component() { Resources.Objects.Add(this); }
    public GameObject gameObject = new GameObject();
    public Transform transform { get { return gameObject.transform; } }
    public Dictionary<Type, Component> Components = new Dictionary<Type, Component>();

    public Component GetComponent(Type type) {
        Component found;
        return Components.TryGetValue(type, out found) ? found : null;
    }
}

public static class Resources {
    public static List<Object> Objects = new List<Object>();

    public static Object[] FindObjectsOfTypeAll(Type type) {
        return Objects.FindAll(o => type.IsInstanceOfType(o)).ToArray();
    }
}

public class Behaviour : Component {
    public bool enabled = true;
}

public struct Vector3 {
    public float x, y, z;

    public Vector3(float xx, float yy, float zz) {
        x = xx;
        y = yy;
        z = zz;
    }

    public static Vector3 operator * (Vector3 value, float scale) {
        return new Vector3(value.x * scale, value.y * scale, value.z * scale);
    }
}

public static class Mathf {
    public static float Sin(float value) { return (float)Math.Sin(value); }
    public static float Cos(float value) { return (float)Math.Cos(value); }
}

public static class Time {
    public static float realtimeSinceStartup;
}

namespace Events {
public delegate void UnityAction();

public class UnityEvent {
    public event UnityAction Click;
    public void AddListener(UnityAction listener) { Click += listener; }
    public void RemoveListener(UnityAction listener) { Click -= listener; }
    public void Invoke() { if (Click != null)Click(); }
}
}
}
