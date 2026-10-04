using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FloorTrack.Twin
{
    /// <summary>
    /// Desktop viewer controls + HUD, zero setup. Put on the Main Camera.
    ///   Left-drag: orbit · Right/middle-drag: pan · Wheel: zoom · F: first-person walk (WASD + mouse) · T: top view
    ///   Hover anything for details (wall, feature, photo camera).
    /// </summary>
    public class TwinViewerControls : MonoBehaviour
    {
        [SerializeField] private TwinWorld world;
        [SerializeField] private TwinClient client;
        [SerializeField] private float orbitSpeed = 0.3f, panSpeed = 0.02f, zoomSpeed = 1.5f, walkSpeed = 2f;

        private Vector3 pivot = Vector3.zero;
        private float yaw = 0f, pitch = 55f, distance = 25f;
        private bool firstPerson;
        private string hover = "";
        private Camera cam;

        private void Awake()
        {
            cam = GetComponent<Camera>();
            if (world == null) world = FindAnyObjectByType<TwinWorld>();
            if (client == null) client = FindAnyObjectByType<TwinClient>();
        }

        private void Update()
        {
            Vector2 delta = In.MouseDelta();
            if (In.KeyDown('F')) { firstPerson = !firstPerson; if (firstPerson) { transform.position = pivot + Vector3.up * 1.6f; pitch = 0; } }
            if (In.KeyDown('T')) { firstPerson = false; pitch = 89f; yaw = 0f; }

            if (firstPerson)
            {
                if (In.Button(1) || In.Button(0)) { yaw += delta.x * orbitSpeed; pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, -80, 80); }
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                Vector3 mv = fwd * In.Axis('W', 'S') + transform.right * In.Axis('D', 'A');
                transform.position += mv * walkSpeed * Time.deltaTime;
            }
            else
            {
                if (In.Button(0)) { yaw += delta.x * orbitSpeed; pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, 5, 89); }
                if (In.Button(1) || In.Button(2))
                    pivot -= (transform.right * delta.x + Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized * delta.y) * panSpeed * distance * 0.05f;
                distance = Mathf.Clamp(distance * Mathf.Pow(0.9f, In.Scroll() * zoomSpeed), 1f, 200f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
                transform.position = pivot - transform.forward * distance;
            }

            hover = "";
            if (cam != null && Physics.Raycast(cam.ScreenPointToRay(In.MousePosition()), out var hit, 500f))
            {
                var info = hit.collider.GetComponentInParent<TwinInfo>();
                if (info != null) hover = info.Text;
            }
        }

        private void OnGUI()
        {
            var s = client != null ? client.State : null;
            string status = client == null ? "no TwinClient" :
                client.LastError != null ? $"server: {client.LastError}" :
                s == null ? "waiting for server…" :
                $"v{s.version} · {s.walls?.Length ?? 0} walls · {s.photos?.Length ?? 0} photos ({Count(s, "done")} done, {Count(s, "queued") + Count(s, "processing")} pending, {Count(s, "error")} errors)";
            string changed = world != null && world.LastChanged.Count > 0 ? "\nlast update changed: " + string.Join(", ", world.LastChanged) : "";
            GUI.Box(new Rect(10, 10, 520, 64), status + changed + "\n[F] walk  [T] top  drag/wheel to orbit");
            if (!string.IsNullOrEmpty(hover))
            {
                Vector2 m = In.MousePosition();
                GUI.Box(new Rect(m.x + 16, Screen.height - m.y + 8, 360, 48), hover);
            }
        }

        private static int Count(TwinStateDto s, string status)
        {
            int n = 0;
            if (s.photos != null) foreach (var p in s.photos) if (p.status == status) n++;
            return n;
        }

        // Works with either input backend.
        private static class In
        {
#if ENABLE_INPUT_SYSTEM
            public static Vector2 MouseDelta() => Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
            public static Vector2 MousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            public static float Scroll()
            {
                float y = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
                return y == 0f ? 0f : Mathf.Sign(y);
            }
            public static bool Button(int b)
            {
                var m = Mouse.current;
                if (m == null) return false;
                return b == 0 ? m.leftButton.isPressed : b == 1 ? m.rightButton.isPressed : m.middleButton.isPressed;
            }
            static UnityEngine.InputSystem.Controls.KeyControl K(char c)
            {
                var kb = Keyboard.current;
                if (kb == null) return null;
                switch (c) { case 'W': return kb.wKey; case 'A': return kb.aKey; case 'S': return kb.sKey; case 'D': return kb.dKey; case 'F': return kb.fKey; case 'T': return kb.tKey; }
                return null;
            }
            public static bool KeyDown(char c) { var k = K(c); return k != null && k.wasPressedThisFrame; }
            public static float Axis(char pos, char neg) { var p = K(pos); var n = K(neg); return (p != null && p.isPressed ? 1 : 0) - (n != null && n.isPressed ? 1 : 0); }
#else
            public static Vector2 MouseDelta() => new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
            public static Vector2 MousePosition() => Input.mousePosition;
            public static float Scroll() => Input.mouseScrollDelta.y;
            public static bool Button(int b) => Input.GetMouseButton(b);
            static KeyCode K(char c) => (KeyCode)System.Enum.Parse(typeof(KeyCode), c.ToString());
            public static bool KeyDown(char c) => Input.GetKeyDown(K(c));
            public static float Axis(char pos, char neg) => (Input.GetKey(K(pos)) ? 1 : 0) - (Input.GetKey(K(neg)) ? 1 : 0);
#endif
        }
    }
}
