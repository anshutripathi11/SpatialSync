using System.Collections.Generic;
using UnityEngine;

namespace FloorTrack.Twin
{
    /// <summary>
    /// Builds and live-updates the 3D twin from server state. Needs NO scene wiring: put it on an empty
    /// GameObject with a TwinClient and press Play. Materials are optional (auto-created if left empty).
    ///
    /// Each wall is a GameObject "Wall_W012" with:
    ///   Solid      - box mesh with doors/windows cut out, coloured from the photos
    ///   Photo_*    - projected photo patches, one per stretch of wall a photo covers best
    ///   Feature_*  - small plaques for things on the wall (signs, extinguishers...)
    /// Only walls whose `rev` changed are rebuilt, and they flash briefly so you can see what each photo did.
    /// </summary>
    [RequireComponent(typeof(TwinClient))]
    public class TwinWorld : MonoBehaviour
    {
        [Header("Materials (optional)")]
        [SerializeField] private Material wallMaterial;
        [SerializeField] private Material glassMaterial;
        [SerializeField] private Material photoMaterial;   // unlit, textured
        [SerializeField] private Material floorMaterial;   // unlit, textured

        [Header("Look")]
        [SerializeField] private Color unobservedWallColor = new Color(0.82f, 0.82f, 0.85f);
        [SerializeField] private Color flashColor = new Color(1f, 0.85f, 0.2f);
        [SerializeField] private bool showPhotoCameras = true;

        private TwinClient client;
        private TwinGeometry geo;
        private Transform root, floor, camerasRoot;
        private readonly Dictionary<string, (int rev, GameObject go)> walls = new Dictionary<string, (int, GameObject)>();
        private readonly Dictionary<string, PhotoDto> photos = new Dictionary<string, PhotoDto>();
        private string builtPlan;

        public List<string> LastChanged { get; } = new List<string>();

        private void Awake()
        {
            client = GetComponent<TwinClient>();
            root = new GameObject("Twin").transform;
            camerasRoot = new GameObject("PhotoCameras").transform;
            camerasRoot.SetParent(root, false);
            EnsureMaterials();
        }

        private void OnEnable() => GetComponent<TwinClient>().StateChanged += Apply;
        private void OnDisable() => GetComponent<TwinClient>().StateChanged -= Apply;

        // -------------------------------------------------------------------------------- apply state
        private void Apply(TwinStateDto s)
        {
            if (s.walls == null) return;
            geo = new TwinGeometry(s);

            photos.Clear();
            if (s.photos != null) foreach (var p in s.photos) photos[p.id] = p;

            if (builtPlan != s.plan_file + s.px_per_m) { RebuildAll(); BuildFloor(s); builtPlan = s.plan_file + s.px_per_m; }

            LastChanged.Clear();
            var alive = new HashSet<string>();
            foreach (var w in s.walls)
            {
                alive.Add(w.id);
                bool existed = walls.TryGetValue(w.id, out var e);
                if (existed && e.rev == w.rev) continue;          // untouched by the last photo
                if (existed && e.go != null) Destroy(e.go);
                var go = BuildWall(w, s.wall_height_m);
                walls[w.id] = (w.rev, go);
                if (existed) { LastChanged.Add(w.id); Flash(go); }
            }
            foreach (var id in new List<string>(walls.Keys))
                if (!alive.Contains(id)) { Destroy(walls[id].go); walls.Remove(id); }

            if (showPhotoCameras) BuildPhotoCameras();
        }

        private void RebuildAll()
        {
            foreach (var e in walls.Values) if (e.go != null) Destroy(e.go);
            walls.Clear();
        }

        // -------------------------------------------------------------------------------- walls
        private GameObject BuildWall(WallDto w, float defaultHeight)
        {
            float height = w.height_m > 0 ? w.height_m : defaultHeight;
            var frame = new WallMeshBuilder.Frame(geo.PxToWorld(w.a), geo.PxToWorld(w.b), w.thickness_px / geo.PxPerM, height);

            var openings = new List<WallMeshBuilder.Opening>();
            if (w.features != null)
                foreach (var f in w.features)
                    if (f.cuts_wall)
                        openings.Add(new WallMeshBuilder.Opening
                        {
                            s0 = Mathf.Min(f.t0, f.t1) * frame.Length, s1 = Mathf.Max(f.t0, f.t1) * frame.Length,
                            bottom = f.bottom_m, top = f.top_m
                        });

            var go = new GameObject($"Wall_{w.id}");
            go.transform.SetParent(root, false);
            var info = go.AddComponent<TwinInfo>();
            info.Text = $"{w.id}  {(w.observed ? $"{w.material}, {w.color_hex}" : "from floor plan only")}\n" +
                        $"{frame.Length:0.0} m long, {(w.features?.Length ?? 0)} features";

            // Solid wall
            var solid = NewChild(go, "Solid", WallMeshBuilder.BuildSolid(frame, openings),
                                 w.material == "glass" ? glassMaterial : wallMaterial);
            var c = w.observed && ColorUtility.TryParseHtmlString(w.color_hex, out var col) ? col : unobservedWallColor;
            SetColor(solid.GetComponent<Renderer>().material, w.material == "glass" ? new Color(0.6f, 0.8f, 0.9f, 0.3f) : c);
            var mc = solid.AddComponent<MeshCollider>();
            mc.sharedMesh = solid.GetComponent<MeshFilter>().sharedMesh;
            solid.AddComponent<TwinInfo>().Text = info.Text;

            // Projected photos: only the stretches this wall was seen in
            if (w.coverage != null && w.material != "glass")
                foreach (var cov in w.coverage)
                {
                    if (!photos.TryGetValue(cov.photo_id, out var p)) continue;
                    var cam = new PhotoCamera(p, geo);
                    var patchMesh = WallMeshBuilder.BuildPhotoPatch(frame, cam,
                        Mathf.Min(cov.t0, cov.t1) * frame.Length, Mathf.Max(cov.t0, cov.t1) * frame.Length, openings);
                    if (patchMesh.vertexCount == 0) continue;
                    var patch = NewChild(go, $"Photo_{cov.photo_id}", patchMesh, photoMaterial);
                    var r = patch.GetComponent<Renderer>();
                    r.enabled = false; // show once the texture arrives
                    client.GetPhoto(cov.photo_id, tex =>
                    {
                        if (r == null) return;    // wall was rebuilt meanwhile
                        SetTexture(r.material, tex);
                        r.enabled = true;
                    });
                }

            // Non-cutting features as plaques on the face the photo was taken from
            if (w.features != null)
                foreach (var f in w.features)
                {
                    if (f.cuts_wall) continue;
                    float side = 1f;
                    if (f.photo_id != null && photos.TryGetValue(f.photo_id, out var fp))
                        side = Mathf.Sign(Vector3.Dot(geo.PxToWorld(fp.x, fp.y) - frame.A, frame.N));
                    BuildPlaque(go, frame, f, side);
                }
            return go;
        }

        private void BuildPlaque(GameObject parent, WallMeshBuilder.Frame frame, FeatureDto f, float side)
        {
            float s0 = Mathf.Min(f.t0, f.t1) * frame.Length, s1 = Mathf.Max(f.t0, f.t1) * frame.Length;
            float off = side * (frame.Thickness * 0.5f);
            var md = new WallMeshBuilder.MeshData();
            var plaqueFrame = new WallMeshBuilder.Frame(frame.A + frame.N * (off + side * 0.02f),
                                                        frame.A + frame.N * (off + side * 0.02f) + frame.U * frame.Length,
                                                        0.03f, frame.Height);
            md.AddBox(plaqueFrame, s0, s1, f.bottom_m, Mathf.Max(f.bottom_m + 0.05f, f.top_m), 0.015f);
            var go = NewChild(parent, $"Feature_{f.type}", md.ToMesh("Plaque"), wallMaterial);
            SetColor(go.GetComponent<Renderer>().material, FeatureColor(f.type));
            go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            go.AddComponent<TwinInfo>().Text = $"{f.type}: {f.label}{(string.IsNullOrEmpty(f.state) ? "" : $" ({f.state})")}\nfrom photo {f.photo_id}";
        }

        private static Color FeatureColor(string type)
        {
            switch (type)
            {
                case "fire_extinguisher": return new Color(0.9f, 0.1f, 0.1f);
                case "sign": return new Color(0.1f, 0.6f, 0.2f);
                case "whiteboard": return Color.white;
                case "screen": return new Color(0.1f, 0.1f, 0.1f);
                case "damage": return new Color(0.6f, 0.3f, 0f);
                default: return new Color(0.2f, 0.4f, 0.9f);
            }
        }

        // -------------------------------------------------------------------------------- floor + cameras
        private void BuildFloor(TwinStateDto s)
        {
            if (floor != null) Destroy(floor.gameObject);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "FloorPlan";
            Destroy(go.GetComponent<Collider>());
            floor = go.transform;
            floor.SetParent(root, false);
            float w = s.plan_width / geo.PxPerM, h = s.plan_height / geo.PxPerM;
            Vector3 center = geo.PxToWorld(s.plan_width * 0.5f, s.plan_height * 0.5f, -0.005f);
            floor.position = center;
            floor.rotation = Quaternion.Euler(90, 0, 0);   // quad faces up, image-up = +Z
            floor.localScale = new Vector3(w, h, 1);
            var r = go.GetComponent<Renderer>();
            r.material = new Material(floorMaterial);
            client.GetPlan(tex => { if (r != null) SetTexture(r.material, tex); });
        }

        private void BuildPhotoCameras()
        {
            foreach (Transform t in camerasRoot) Destroy(t.gameObject);
            foreach (var p in photos.Values)
            {
                var cam = new PhotoCamera(p, geo);
                var go = new GameObject($"Cam_{p.id}");
                go.transform.SetParent(camerasRoot, false);
                go.AddComponent<TwinInfo>().Text = $"photo {p.id}: {p.status} {p.error}\nsees {string.Join(", ", p.walls_seen ?? new string[0])}";
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.widthMultiplier = 0.03f;
                lr.material = new Material(photoMaterial);
                SetColor(lr.material, p.status == "done" ? Color.green : p.status == "error" ? Color.red : Color.yellow);
                float d = 0.6f;
                Vector3 C = cam.Position, F = cam.Forward * d, R = cam.Right * d * cam.TanH, U = cam.Up * d * cam.TanV;
                Vector3 c1 = C + F - R - U, c2 = C + F + R - U, c3 = C + F + R + U, c4 = C + F - R + U;
                var pts = new[] { C, c1, c2, C, c3, c4, C, c1, c4, c3, c2 };
                lr.positionCount = pts.Length;
                lr.SetPositions(pts);
                var col = go.AddComponent<SphereCollider>();
                col.center = C; col.radius = 0.25f;
            }
        }

        // -------------------------------------------------------------------------------- helpers
        private GameObject NewChild(GameObject parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().material = new Material(mat);
            return go;
        }

        private void Flash(GameObject wall)
        {
            var solid = wall.transform.Find("Solid");
            if (solid != null) solid.gameObject.AddComponent<TwinFlash>().Init(flashColor);
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static Color GetColor(Material m) =>
            m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;

        private static void SetTexture(Material m, Texture t)
        {
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
        }

        private void EnsureMaterials()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            if (wallMaterial == null) wallMaterial = new Material(lit);
            if (photoMaterial == null) photoMaterial = new Material(unlit);
            if (floorMaterial == null) floorMaterial = new Material(unlit);
            if (glassMaterial == null)
            {
                glassMaterial = new Material(lit);
                // Transparent setup differs per pipeline; assign a real glass material in the Inspector for best results.
                if (glassMaterial.HasProperty("_Surface")) glassMaterial.SetFloat("_Surface", 1f);
            }
        }
    }

    /// <summary>Hover/click text for the HUD.</summary>
    public class TwinInfo : MonoBehaviour
    {
        [TextArea] public string Text;
    }

    /// <summary>Fades a freshly updated wall from the flash colour back to its own colour.</summary>
    public class TwinFlash : MonoBehaviour
    {
        private Material mat;
        private Color from, to;
        private float t;

        public void Init(Color flash)
        {
            mat = GetComponent<Renderer>().material;
            to = TwinWorld.GetColor(mat);
            from = flash;
        }

        private void Update()
        {
            t += Time.deltaTime / 1.5f;
            TwinWorld.SetColor(mat, Color.Lerp(from, to, t));
            if (t >= 1f) Destroy(this);
        }
    }
}
