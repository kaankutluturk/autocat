using System;
using System.Collections;
using System.Collections.Generic;

// The slice of Unity the game component and the PawPass fixture need. A destroyed object equals null, as in Unity.
namespace UnityEngine {
public class Object {
    public bool Destroyed;
    public string name = "";
    public static void Destroy(Object value) { if (!System.Object.ReferenceEquals(value, null))value.Destroyed = true; }
    public static void DontDestroyOnLoad(Object value) { }

    public static bool operator == (Object a, Object b) {
        bool an = System.Object.ReferenceEquals(a, null) || a.Destroyed;
        bool bn = System.Object.ReferenceEquals(b, null) || b.Destroyed;
        return an || bn ? an == bn : System.Object.ReferenceEquals(a, b);
    }

    public static bool operator != (Object a, Object b) { return !(a == b); }
    public static implicit operator bool(Object value) { return value != null; }
    public override bool Equals(object obj) { return System.Object.ReferenceEquals(this, obj); }
    public override int GetHashCode() { return base.GetHashCode(); }
}

public class GameObject : Object {
    public bool activeSelf = true;
    public bool activeInHierarchy { get { return activeSelf; } }
    public GameObject() { Resources.Objects.Add(this); }
    public GameObject(string title) : this() { name = title; }
    public void SetActive(bool active) { activeSelf = active; }

    public T AddComponent<T>() where T : Component, new() {
        var component = new T();
        component.gameObject = this;
        return component;
    }
}

public class Component : Object {
    public GameObject gameObject = new GameObject();
}

public class Behaviour : Component {
}

public class Coroutine {
}

public class MonoBehaviour : Behaviour {
    public static readonly List<IEnumerator> Started = new List<IEnumerator>();

    public Coroutine StartCoroutine(IEnumerator routine) {
        Started.Add(routine);
        return new Coroutine();
    }
}

public class ScriptableObject : Object {
}

public static class Resources {
    public static readonly List<Object> Objects = new List<Object>();

    public static Object[] FindObjectsOfTypeAll(Type type) {
        return Objects.FindAll(o => type.IsInstanceOfType(o)).ToArray();
    }
}

public static class Time {
    public static float realtimeSinceStartup;
}

public static class PlayerPrefs {
    public static string GetString(string key, string fallback) { return fallback; }
}

public static class Mathf {
    // As Unity does it, which matters when high is below low.
    public static int Clamp(int value, int low, int high) { return value < low ? low : value > high ? high : value; }
}
}
