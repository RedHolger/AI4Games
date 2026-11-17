using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orchard
{
    // draws the board exactly like the html canvas: every frame, in the same order,
    // into a 704x704 render texture. each draw call gets the next sorting order (painter's algorithm).
    // canvas coords (x right, y down) map to world (x, -y).
    public class GameRenderer
    {
        public const int LAYER = 31;
        public RenderTexture rt;

        Camera cam;
        Transform root;
        Material mat;
        Dictionary<string, Sprite> img;
        Sprite circle, pixel, tuft;

        List<SpriteRenderer> pool = new List<SpriteRenderer>();
        int used;
        List<LineRenderer> lpool = new List<LineRenderer>();
        int lused;
        int order;
        Vector3[] arcPts = new Vector3[21];

        // dashed stalker ring, precomputed relative to the stalker (setLineDash([6,5]))
        List<Vector4> dashes = new List<Vector4>();

        struct Item
        {
            public double y;
            public int seq;
            public int k;      // 0 hill, 1 bush, 2 apple, 3 tree, 4 enemy, 5 dog, 6 farmer
            public double x, h;
            public object refObj;
            public string imgName;
        }
        List<Item> items = new List<Item>();

        public GameRenderer(Dictionary<string, Sprite> img)
        {
            this.img = img;

            rt = MakeRT(C.VW, C.VH);

            GameObject camGo = new GameObject("Orchard Board Camera");
            cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = C.VH / 2f;
            cam.transform.position = new Vector3(C.VW / 2f, -C.VH / 2f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = U.hex(0x2f4a27);
            cam.cullingMask = 1 << LAYER;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.allowMSAA = true;
            cam.allowHDR = false;
            cam.depth = -50;
            cam.targetTexture = rt;

            root = new GameObject("Orchard Board").transform;

            Shader sh = null;
            if (GraphicsSettings.currentRenderPipeline != null)
                sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mat = new Material(sh);

            circle = Shapes.Circle();
            pixel = Shapes.Pixel();
            tuft = Shapes.Tuft();
            BuildDashes(220 * 0.55f, 220 * 0.45f, 6, 5);
        }

        static RenderTexture MakeRT(int w, int h)
        {
            RenderTexture t = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            t.antiAliasing = 4;
            t.filterMode = FilterMode.Bilinear;
            t.Create();
            return t;
        }

        // render the board at exactly the pixels it covers on screen (retina, 1080p, 4k...),
        // so nothing is ever stretched and blurred. the camera keeps showing the same 704 px of
        // world height, it just draws them with more pixels.
        public bool Resize(int w, int h)
        {
            if (rt != null && rt.width == w && rt.height == h) return false;
            RenderTexture old = rt;
            rt = MakeRT(w, h);
            cam.targetTexture = rt;
            if (old != null) { old.Release(); Object.Destroy(old); }
            return true;
        }

        // ---------- immediate-mode drawing helpers ----------
        SpriteRenderer Next()
        {
            SpriteRenderer r;
            if (used < pool.Count) r = pool[used];
            else
            {
                GameObject go = new GameObject("d");
                go.layer = LAYER;
                go.transform.SetParent(root, false);
                r = go.AddComponent<SpriteRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                pool.Add(r);
            }
            used++;
            if (!r.enabled) r.enabled = true;
            r.sortingOrder = order++;
            return r;
        }

        LineRenderer NextLine()
        {
            LineRenderer l;
            if (lused < lpool.Count) l = lpool[lused];
            else
            {
                GameObject go = new GameObject("a");
                go.layer = LAYER;
                go.transform.SetParent(root, false);
                l = go.AddComponent<LineRenderer>();
                l.sharedMaterial = mat;
                l.useWorldSpace = true;
                l.numCapVertices = 0;
                l.numCornerVertices = 0;
                l.alignment = LineAlignment.View;
                l.textureMode = LineTextureMode.Stretch;
                l.shadowCastingMode = ShadowCastingMode.Off;
                l.receiveShadows = false;
                lpool.Add(l);
            }
            lused++;
            if (!l.enabled) l.enabled = true;
            l.sortingOrder = order++;
            return l;
        }

        // ctx.drawImage(img, sx, sy, w, h) (optionally mirrored)
        void DrawImage(Sprite s, double sx, double sy, double w, double h, bool flip, double alpha)
        {
            SpriteRenderer r = Next();
            r.sprite = s;
            r.flipX = flip;
            r.color = new Color(1, 1, 1, (float)alpha);
            Transform t = r.transform;
            t.localPosition = new Vector3((float)(sx + w / 2.0), (float)(-(sy + h / 2.0)), 0);
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3((float)(w / s.rect.width), (float)(h / s.rect.height), 1);
        }

        // image centred on (cx, cy), rotated by rot radians (clockwise on screen, like canvas)
        void DrawImageRot(Sprite s, double cx, double cy, double w, double h, double rot, bool flip, double alpha)
        {
            SpriteRenderer r = Next();
            r.sprite = s;
            r.flipX = flip;
            r.color = new Color(1, 1, 1, (float)alpha);
            Transform t = r.transform;
            t.localPosition = new Vector3((float)cx, (float)(-cy), 0);
            t.localRotation = Quaternion.Euler(0, 0, (float)(-rot * 180.0 / System.Math.PI));
            t.localScale = new Vector3((float)(w / s.rect.width), (float)(h / s.rect.height), 1);
        }

        // the html spr(): width from aspect, top-left rounded like Math.round
        void Spr(string name, double x, double y, double h, bool flip, bool anchorBottom, double alpha = 1)
        {
            Sprite s;
            if (!img.TryGetValue(name, out s) || s == null) return;
            double w = h * s.rect.width / s.rect.height;
            double sx = U.jsRound(x - w / 2);
            double sy = U.jsRound(anchorBottom ? y - h : y - h / 2);
            DrawImage(s, sx, sy, w, h, flip, alpha);
        }

        void FillEllipse(double cx, double cy, double rx, double ry, Color c)
        {
            SpriteRenderer r = Next();
            r.sprite = circle;
            r.flipX = false;
            r.color = c;
            Transform t = r.transform;
            t.localPosition = new Vector3((float)cx, (float)(-cy), 0);
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3((float)(rx * 2), (float)(ry * 2), 1);
        }

        void FillRect(double x, double y, double w, double h, Color c)
        {
            SpriteRenderer r = Next();
            r.sprite = pixel;
            r.flipX = false;
            r.color = c;
            Transform t = r.transform;
            t.localPosition = new Vector3((float)(x + w / 2.0), (float)(-(y + h / 2.0)), 0);
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3((float)w, (float)h, 1);
        }

        // a straight stroke with butt caps (canvas default)
        void Line(double x1, double y1, double x2, double y2, double width, Color c)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = System.Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return;
            SpriteRenderer r = Next();
            r.sprite = pixel;
            r.flipX = false;
            r.color = c;
            Transform t = r.transform;
            t.localPosition = new Vector3((float)((x1 + x2) / 2.0), (float)(-(y1 + y2) / 2.0), 0);
            t.localRotation = Quaternion.Euler(0, 0, (float)(System.Math.Atan2(-dy, dx) * 180.0 / System.Math.PI));
            t.localScale = new Vector3((float)len, (float)width, 1);
        }

        // ctx.arc(cx, cy, R, a0, a1) stroked
        void Arc(double cx, double cy, double R, double a0, double a1, double width, Color c)
        {
            LineRenderer l = NextLine();
            int n = arcPts.Length - 1;
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                arcPts[i] = new Vector3((float)(cx + System.Math.Cos(a) * R), (float)(-(cy + System.Math.Sin(a) * R)), 0);
            }
            l.positionCount = arcPts.Length;
            l.SetPositions(arcPts);
            l.startWidth = (float)width;
            l.endWidth = (float)width;
            l.startColor = c;
            l.endColor = c;
        }

        void DrawTuft(double x, double y)
        {
            SpriteRenderer r = Next();
            r.sprite = tuft;
            r.flipX = false;
            r.color = Color.white;
            Transform t = r.transform;
            t.localPosition = new Vector3((float)x, (float)(-y), 0);
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        void BuildDashes(float rx, float ry, float on, float off)
        {
            int M = 4000;
            Vector2[] p = new Vector2[M + 1];
            float[] s = new float[M + 1];
            for (int i = 0; i <= M; i++)
            {
                float a = Mathf.PI * 2 * i / M;
                p[i] = new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
                if (i > 0) s[i] = s[i - 1] + Vector2.Distance(p[i - 1], p[i]);
            }
            float total = s[M];
            for (float s0 = 0; s0 < total; s0 += on + off)
            {
                float s1 = Mathf.Min(s0 + on, total);
                Vector2 a = PointAt(p, s, s0), b = PointAt(p, s, s1);
                dashes.Add(new Vector4(a.x, a.y, b.x, b.y));
            }
        }

        static Vector2 PointAt(Vector2[] p, float[] s, float d)
        {
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] >= d)
                {
                    float seg = s[i] - s[i - 1];
                    float t = seg > 0 ? (d - s[i - 1]) / seg : 0;
                    return Vector2.Lerp(p[i - 1], p[i], t);
                }
            }
            return p[p.Length - 1];
        }

        void Begin()
        {
            used = 0;
            lused = 0;
            order = -32000;
        }

        void End()
        {
            for (int i = used; i < pool.Count; i++) if (pool[i].enabled) pool[i].enabled = false;
            for (int i = lused; i < lpool.Count; i++) if (lpool[i].enabled) lpool[i].enabled = false;
        }

        // ================= visual layer (draw only, never touches game state) =================
        double vt;

        static double vhash(int a, int b, int c)
        {
            unchecked
            {
                uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ (uint)(c * 83492791);
                h = (h ^ (h >> 13)) * 1274126177u;
                h = h ^ (h >> 16);
                return h / 4294967296.0;
            }
        }

        static Color Col(int r, int g, int b, double a = 1) { return new Color(r / 255f, g / 255f, b / 255f, (float)a); }
        static Color WithA(Color c, double mul) { return new Color(c.r, c.g, c.b, (float)(c.a * mul)); }

        void E(double cx, double cy, double rx, double ry, Color c) { FillEllipse(cx, cy, rx, ry, c); }

        void StrokeEllipse(double cx, double cy, double rx, double ry, double width, Color c)
        {
            LineRenderer l = NextLine();
            int n = arcPts.Length - 1;
            for (int i = 0; i <= n; i++)
            {
                double a = System.Math.PI * 2 * i / n;
                arcPts[i] = new Vector3((float)(cx + System.Math.Cos(a) * rx), (float)(-(cy + System.Math.Sin(a) * ry)), 0);
            }
            l.positionCount = arcPts.Length;
            l.SetPositions(arcPts);
            l.startWidth = (float)width;
            l.endWidth = (float)width;
            l.startColor = c;
            l.endColor = c;
        }

        void Pill(double x, double y, double w, double h, Color c)
        {
            double r = h / 2;
            if (w < h) { E(x + w / 2, y + r, w / 2, r, c); return; }
            FillRect(x + r, y, w - h, h, c);
            E(x + r, y + r, r, r, c);
            E(x + w - r, y + r, r, r, c);
        }

        static Color HpCol(double f) { return f > 0.6 ? U.hex(0x8fd36a) : (f > 0.3 ? U.hex(0xe3b341) : U.hex(0xe0573e)); }

        static bool CellIs(OrchardGame g, int x, int y, int v) { return x >= 0 && y >= 0 && x < C.GW && y < C.GH && g.grid[x, y] == v; }

        // a cell plus bridges to its right / lower / diagonal neighbours, so blobs merge into one shape
        void ForBlobs(OrchardGame g, int v, System.Action<double, double, int, int, int> fn)
        {
            for (int x = 0; x < C.GW; x++)
                for (int y = 0; y < C.GH; y++)
                    if (CellIs(g, x, y, v))
                    {
                        Pos c = U.cc(x, y);
                        fn(c.x, c.y, x, y, 1);
                        if (CellIs(g, x + 1, y, v)) fn(c.x + C.CELL / 2, c.y, x, y, 2);
                        if (CellIs(g, x, y + 1, v)) fn(c.x, c.y + C.CELL / 2, x, y, 3);
                        if (CellIs(g, x + 1, y + 1, v) && CellIs(g, x + 1, y, v) && CellIs(g, x, y + 1, v)) fn(c.x + C.CELL / 2, c.y + C.CELL / 2, x, y, 4);
                    }
        }

        void DrawGround()
        {
            FillRect(0, 0, C.VW, C.VH, U.hex(0x4c7a38));    // flat grass, no checkerboard
        }

        void DrawMud(OrchardGame g)
        {
            Color rim = U.hex(0x3c2b18), body = U.hex(0x5b4327), inner = U.hex(0x4a3820);
            ForBlobs(g, C.MUD, (x, y, cx, cy, k) => E(x, y + 2, 25, 18, rim));
            ForBlobs(g, C.MUD, (x, y, cx, cy, k) => E(x, y, 23, 16, body));
            ForBlobs(g, C.MUD, (x, y, cx, cy, k) => E(x, y + 1, 17, 11, inner));
                    }

        // fallen logs: a bark-brown trunk across two cells, cut rings at both ends, a leafy twig
        void DrawLogs(OrchardGame g)
        {
            if (g.logs == null) return;
            Color dark = U.hex(0x4a3019), bark = U.hex(0x6e4a28), hi = U.hex(0x8c6438);
            Color ring = U.hex(0xd9b77e), ringIn = U.hex(0xb48a52), leaf = U.hex(0x5f8f3a), leaf2 = U.hex(0x7aa84a);
            foreach (FallenLog lg in g.logs)
            {
                Pos a = U.cc(lg.cx, lg.cy);
                Pos b = lg.horiz ? U.cc(lg.cx + 1, lg.cy) : U.cc(lg.cx, lg.cy + 1);
                double tilt = (lg.seed - 0.5) * 8;                 // a little crooked, not ruler-straight
                double ax = a.x + (lg.horiz ? -17 : tilt), ay = a.y + (lg.horiz ? tilt : -17);
                double bx = b.x + (lg.horiz ? 17 : -tilt), by = b.y + (lg.horiz ? -tilt : 17);
                Line(ax, ay + 2, bx, by + 2, 24, dark);              // underside
                Line(ax, ay, bx, by, 20, bark);                      // trunk
                Line(ax - (lg.horiz ? 0 : 5), ay - (lg.horiz ? 5 : 0), bx - (lg.horiz ? 0 : 5), by - (lg.horiz ? 5 : 0), 5, hi);                 // light on top
                double len = U.hypot(bx - ax, by - ay), ux = (bx - ax) / len, uy = (by - ay) / len;
                for (int k = 1; k <= 3; k++)                         // bark grooves
                {
                    double t = k / 4.0, px = ax + (bx - ax) * t, py = ay + (by - ay) * t;
                    Line(px - uy * 8 - ux * 4, py + ux * 8 - uy * 4, px + uy * 6 + ux * 3, py - ux * 6 + uy * 3, 2, dark);
                }
                E(bx, by, 10.5, 10.5, ring);                           // cut end
                E(bx, by, 6.5, 6.5, ringIn);
                E(bx, by, 2.2, 2.2, dark);
                E(ax, ay, 10, 10, dark);                               // broken end
                double mx = (ax + bx) / 2 + uy * 9, my = (ay + by) / 2 - ux * 9;
                Line((ax + bx) / 2, (ay + by) / 2, mx - ux * 6 + uy * 6, my - uy * 6 - ux * 6, 2.4, bark);   // twig
                E(mx - ux * 6 + uy * 8, my - uy * 6 - ux * 8, 6.5, 4.5, leaf);
                E(mx - ux * 1 + uy * 11, my - uy * 1 - ux * 11, 4.5, 3.2, leaf2);
            }
        }

        void DrawHills(OrchardGame g)
        {
            Color rim = U.hex(0x5a5246), body = U.hex(0x857b6a), top = U.hex(0x9b917d), hi = U.hex(0xb3a993), moss = Col(80, 120, 50, .75);
            ForBlobs(g, C.HILL, (x, y, cx, cy, k) => E(x, y + 3, 26, 20, rim));
            ForBlobs(g, C.HILL, (x, y, cx, cy, k) => E(x, y, 24, 18, body));
            ForBlobs(g, C.HILL, (x, y, cx, cy, k) => E(x - 3, y - 4, 18, 12, top));
            ForBlobs(g, C.HILL, (x, y, cx, cy, k) => { if (k == 1) { E(x - 8, y - 8, 8, 4, hi); E(x + 10, y + 6, 6, 3, moss); E(x - 12, y + 9, 5, 2.5, moss); } });
            Color s1 = U.hex(0x4f483d), s2 = U.hex(0xa8a08c), s3 = U.hex(0xc8c0aa);
            for (int x = 0; x < C.GW; x++)
                for (int y = 0; y < C.GH; y++)
                    if (g.grid[x, y] == C.HILL)
                    {
                        Pos c = U.cc(x, y);
                        int n = 1 + (int)(vhash(x, y, 3) * 2);
                        for (int k = 0; k < n; k++)
                        {
                            double rx = c.x + (vhash(x, y, k * 3 + 4) - .5) * 26, ry = c.y + (vhash(x, y, k * 3 + 5) - .5) * 18, s = 4 + vhash(x, y, k * 3 + 6) * 3;
                            E(rx + 1, ry + 2, s, s * 0.7, s1);
                            E(rx, ry, s, s * 0.7, s2);
                            E(rx - 1, ry - 1, s * 0.5, s * 0.3, s3);
                        }
                    }
        }

        void DrawBush(double x, double y)
        {
            Color d = U.hex(0x244f21), m = U.hex(0x2b5f27), l1 = U.hex(0x3f8438), l2 = U.hex(0x3a7a33), l3 = U.hex(0x55a046), berry = U.hex(0xc83d3d);
            E(x - 9, y - 6, 12, 11, d); E(x + 9, y - 6, 12, 11, d); E(x, y - 14, 14, 13, m);
            E(x - 5, y - 12, 8, 7, l1); E(x + 6, y - 9, 6, 5, l2); E(x - 2, y - 19, 5, 4, l3);
            E(x - 8, y - 4, 2, 2, berry); E(x + 5, y - 15, 2, 2, berry); E(x + 10, y - 3, 2, 2, berry);
        }

        public void Render(OrchardGame g, double dt)
        {
            Begin();
            vt += dt;
            if (g.grid == null) { FillRect(0, 0, C.VW, C.VH, U.hex(0x4e7c3c)); End(); return; }

            DrawGround();
            DrawMud(g);
            foreach (Tuft t in g.tufts) DrawTuft(t.x, t.y);
            Color bc = U.hex(0x5a3f24), bc2 = U.hex(0x6e4e2e);
            foreach (Branch br in g.branches)
            {
                double dx = System.Math.Cos(br.a) * 10, dy = System.Math.Sin(br.a) * 10;
                Line(br.x - dx, br.y - dy, br.x + dx, br.y + dy, 3, bc);
                Line(br.x, br.y, br.x + System.Math.Cos(br.a + 0.6) * 6, br.y + System.Math.Sin(br.a + 0.6) * 6, 2, bc2);
            }
            DrawHills(g);
            DrawLogs(g);

            // depth list
            items.Clear();
            int seq = 0;
            for (int x = 0; x < C.GW; x++)
                for (int y = 0; y < C.GH; y++)
                    if (g.grid[x, y] == C.BUSH) { Pos c = U.cc(x, y); items.Add(MakeItem(c.y, seq++, 1, c.x, 0, null, null)); }
            foreach (Bait b in g.baits) items.Add(MakeItem(b.y, seq++, 7, b.x, 20, null, null));
            foreach (TreeEntity t in g.trees) items.Add(MakeItem(t.y, seq++, 3, 0, 0, t, null));
            foreach (FlyingFruit f in g.flyingFruit) items.Add(MakeItem(f.y, seq++, 2, f.x, 18, null, null));
            foreach (Enemy e in g.enemies) items.Add(MakeItem(e.y, seq++, 4, 0, 0, e, null));
            items.Add(MakeItem(g.pip.y, seq++, 5, 0, 0, g.pip, "pip"));
            items.Add(MakeItem(g.thistle.y, seq++, 5, 0, 0, g.thistle, "thistle"));
            items.Add(MakeItem(g.farmer.y, seq++, 6, 0, 0, g.farmer, null));
            U.StableSort(items, (a, b) => a.y.CompareTo(b.y));

            // stalker detection: red glow + dashed ring
            Color ring = U.rgba(210, 70, 55, .8f), red = Col(220, 60, 45);
            foreach (Enemy e in g.enemies)
            {
                Stalker st = e as Stalker;
                if (st != null && st.sees)
                {
                    foreach (Vector4 d in dashes) Line(e.x + d.x, e.y + d.y, e.x + d.z, e.y + d.w, 2, ring);
                }
            }

            // pip's repair tether (dotted line to the tree it is fixing)
            TreeEntity pt = g.pip.target;
            if (pt != null && pt.alive && pt.hp < pt.maxhp - 0.01)
            {
                double d = U.hypot(pt.x - g.pip.x, pt.y - g.pip.y);
                if (d > 30)
                {
                    int n = (int)System.Math.Floor(d / 14);
                    for (int i = 1; i < n; i++)
                    {
                        double u = (double)i / n;
                        E(g.pip.x + (pt.x - g.pip.x) * u, g.pip.y + (pt.y - g.pip.y) * u, 1.8, 1.8, Col(156, 240, 138, 0.25 + 0.35 * System.Math.Max(0, System.Math.Sin(vt * 6 - i))));
                    }
                }
            }

            // shamblers ganging up on a dog: amber lines to the dog, a red ring once 2+ are biting
            Mover[] dogs = { g.pip, g.thistle };
            foreach (Mover dog in dogs)
            {
                int bitingNow = g.PackSize(dog);
                foreach (Enemy e in g.enemies)
                {
                    Shambler s = e as Shambler;
                    if (s == null || s.act != ShamAct.PackDog || s.dogT != dog) continue;
                    double d = U.hypot(dog.x - s.x, dog.y - s.y);
                    int n = (int)System.Math.Floor(d / 10);
                    for (int i = 1; i < n; i++)
                    {
                        double u = (double)i / n;
                        E(s.x + (dog.x - s.x) * u, s.y + (dog.y - s.y) * u, 1.6, 1.6, Col(233, 184, 80, 0.35 + 0.35 * System.Math.Max(0, System.Math.Sin(vt * 8 - i))));
                    }
                }
                if (bitingNow >= 2)
                {
                    double pr = 26 + 3 * System.Math.Sin(vt * 9);
                    StrokeEllipse(dog.x, dog.y - 4, pr, pr * 0.55, 2.5, Col(240, 90, 70, 0.85));
                }
            }

            Color dark = Col(20, 16, 10, .75), track = Col(255, 255, 255, .12), shine = Col(255, 255, 255, .35);
            foreach (Item it in items)
            {
                if (it.k == 1) DrawBush(it.x, it.y);
                else if (it.k == 7)
                {
                    Spr("apple", it.x, it.y, it.h, false, false);
                }
                else if (it.k == 2)
                {
                    Spr("apple", it.x, it.y, it.h, false, false);
                }
                else if (it.k == 3)
                {
                    TreeEntity t = (TreeEntity)it.refObj;
                    if (t.alive)
                    {
                        Spr("tree", t.x, t.y + 12, 96, false, true);
                        double f = t.hp / t.maxhp, w = 46, hx = t.x - w / 2, hy = t.y - 92;
                        Pill(hx - 1.5, hy - 1.5, w + 3, 9, dark);
                        Pill(hx, hy, w, 6, track);
                        if (f > 0) Pill(hx, hy, System.Math.Max(6, w * f), 6, HpCol(f));
                        Pill(hx + 2, hy + 1, System.Math.Max(0, w * f - 4), 1.5, shine);
                    }
                    else
                    {
                        Spr("tree", t.x, t.y + 12, 74, false, true, .4);
                        E(t.x, t.y + 6, 9, 4, U.hex(0x4b3622));
                        E(t.x, t.y + 4, 8, 3.2, U.hex(0x7a5a3a));
                    }
                }
                else if (it.k == 4)
                {
                    Enemy e = (Enemy)it.refObj;
                    Bird bird = e as Bird;
                    if (bird != null)
                    {
                        double bob = System.Math.Sin(bird.bob) * 3;
                        Spr(bird.bad ? "badscatter" : "scatterling", e.x, e.y - bird.flyH + bob, 28, e.dir < 0, true);
                        if (bird.bad) E(e.x + (e.dir < 0 ? -5 : 5), e.y - bird.flyH + bob - 17, 2.2, 2.2, Col(255, 60, 50, .9));
                        if (bird.confT > 0 || bird.noEatT > 0)
                        {
                            double nx = e.x + 13, ny = e.y - bird.flyH + bob - 28;   // tiny crossed-out apple
                            E(nx, ny, 4, 4, Col(20, 16, 10, .6));
                            E(nx, ny + 0.4, 2.6, 2.4, Col(230, 90, 70, .85));
                            Line(nx - 3.4, ny + 3.4, nx + 3.4, ny - 3.4, 1.3, Col(255, 255, 255, .9));
                        }
                    }
                    else
                    {
                        double bob = System.Math.Abs(System.Math.Sin(e.walk)) * (e.type == "shambler" ? 4 : 3);
                        double h = e.type == "stalker" ? 34 : 44;
                        Stalker st = e as Stalker;
                        Spr(e.type, e.x, e.y + 5 - bob, h, e.dir < 0, true);
                        if (e.hp < 6)
                        {
                            double w = 26;
                            Pill(e.x - w / 2 - 1, e.y - h - 5, w + 2, 5, Col(20, 16, 10, .7));
                            Pill(e.x - w / 2, e.y - h - 4, w * System.Math.Max(0, e.hp) / 6, 3, U.hex(0xe0573e));
                        }
                        Shambler sh = e as Shambler;
                        if (sh != null) DrawIntent(sh, e.x, e.y - h - 15, g.showUtility);
                        if (st != null && st.sees)
                        {
                            double by = e.y - h - 12 + System.Math.Sin(vt * 10) * 1.5;
                            E(e.x, by, 7, 7, U.hex(0xe0573e));
                            FillRect(e.x - 1.2, by - 4.5, 2.4, 6, Color.white);
                            E(e.x, by + 3.8, 1.3, 1.3, Color.white);
                        }
                    }
                }
                else if (it.k == 5)
                {
                    Mover m = (Mover)it.refObj;
                    int dir = (m is Pip) ? ((Pip)m).dir : ((Thistle)m).dir;
                    Spr(it.imgName, m.x, m.y + 4, 34, dir < 0, true);
                    Pip pp = m as Pip;
                    double dhp = pp != null ? pp.hp : ((Thistle)m).hp;
                    double flash = pp != null ? pp.hurtFlash : ((Thistle)m).hurtFlash;
                    if (dhp < 4.99)
                    {
                        double w = 24;
                        Pill(m.x - w / 2 - 1, m.y - 41, w + 2, 5, Col(20, 16, 10, .75));
                        Pill(m.x - w / 2, m.y - 40, w * System.Math.Max(0, dhp) / 5, 3, HpCol(dhp / 5));
                    }
                    if (pp != null && pp.retreating)
                    {
                        // little blue "help!" drop over pip while he runs to the farmer
                        double by = m.y - 50 + System.Math.Sin(vt * 8) * 1.5;
                        E(m.x, by, 6.5, 6.5, Col(20, 16, 10, .75));
                        E(m.x, by + 1, 3.2, 3.6, U.hex(0x7fb4e6));
                        E(m.x, by - 2.2, 1.6, 2.2, U.hex(0x7fb4e6));
                    }
                    Thistle th = m as Thistle;
                    if (th != null && th.state != ThistleState.Wander)
                    {
                        Color c = th.state == ThistleState.Feed ? U.hex(0xe3b341) : U.hex(0x7fb4e6);
                        E(m.x + 12, m.y - 34, 4, 4, Col(20, 16, 10, .7));
                        E(m.x + 12, m.y - 34, 2.6, 2.6, c);
                    }
                }
                else if (it.k == 6)
                {
                    Farmer f = (Farmer)it.refObj;
                    double bob = f.moving ? System.Math.Abs(System.Math.Sin(f.walk)) * 3 : 0;
                    Spr("farmer", f.x, f.y + 5 - bob, 46, f.dir < 0, true);
                    DrawStick(f, bob);
                    if (f.hp < C.FARMER_HP - 0.01)
                    {
                        double w = 30, fr = f.hp / C.FARMER_HP, hy = f.y - 58;
                        Pill(f.x - w / 2 - 1, hy - 1, w + 2, 6, Col(20, 16, 10, .8));
                        Pill(f.x - w / 2, hy, System.Math.Max(4, w * fr), 4, HpCol(fr));
                    }
                }
            }

            foreach (Tornado t in g.tornados) DrawTornado(t);

            End();
        }

        // the farmer's stick: held up, it swings down and back up each time he hits (space)
        void DrawStick(Farmer f, double bob)
        {
            double p = f.swing > 0 ? U.clamp(1 - f.swing / 0.32, 0, 1) : 0;
            double a = -1.15 + 1.95 * System.Math.Sin(System.Math.PI * p);    // -1.15 raised ... +0.8 down
            double d = f.dir < 0 ? -1 : 1, L = 34;
            double hx = f.x + d * 9, hy = f.y - 17 - bob;
            double tx = hx + d * System.Math.Cos(a) * L, ty = hy + System.Math.Sin(a) * L;
            Line(hx, hy, tx, ty, 4, U.hex(0x8a5a2b));
            E(tx, ty, 4.5, 4.5, U.hex(0xe6eae2));
        }

        // tornado: just a plain ring showing its reach on the ground (the confusion zone) and a small core
        void DrawTornado(Tornado t)
        {
            double life = t.life, age = C.TORNADO_LIFE - life;
            double fade = System.Math.Min(1, System.Math.Min(age / 0.5, life / 0.8));
            StrokeEllipse(t.x, t.y, t.r * 0.95, t.r * 0.95 * 0.42, 2, Col(230, 215, 180, 0.7 * fade));
            E(t.x, t.y, 9, 4, Col(170, 150, 115, 0.9 * fade));
        }

        // ---- shambler decision display ----
        public static readonly Color[] ACT_COL = {
            new Color(0x8f / 255f, 0xd3 / 255f, 0x6a / 255f, 1),   // RaidTree  green
            new Color(1f, 0x9f / 255f, 0x43 / 255f, 1),            // EatFruit  orange
            new Color(0xe9 / 255f, 0xb8 / 255f, 0x50 / 255f, 1),   // PackDog   amber
            new Color(0xf0 / 255f, 0x6a / 255f, 0x55 / 255f, 1),   // HitFarmer red
            new Color(0x7f / 255f, 0xb4 / 255f, 0xe6 / 255f, 1),   // Flee      blue
            new Color(0xd8 / 255f, 0xc8 / 255f, 0xa8 / 255f, 1) }; // Whirl     dust

        // a small speech bubble over the shambler showing what it decided, plus (Tab) its desirability bars
        void DrawIntent(Shambler s, double x, double y, bool bars)
        {
            Color dark = Col(20, 16, 10, .78);
            E(x, y, 8.5, 8.5, dark);
            E(x - 3, y + 8, 2.4, 2.4, dark);
            Color c = ACT_COL[(int)s.act];
            if (s.act == ShamAct.RaidTree)
            {
                FillRect(x - 1, y + 0.5, 2, 4.5, U.hex(0x7a5230));
                E(x, y - 1.5, 4.6, 4.2, c);
            }
            else if (s.act == ShamAct.EatFruit)
            {
                E(x, y + 0.8, 4.2, 4, c);
                E(x + 1.8, y - 3.8, 1.8, 1, U.hex(0x8fd36a));
            }
            else if (s.act == ShamAct.PackDog)
            {
                E(x, y + 1.6, 3.2, 2.6, c);                     // paw pad
                E(x - 3.4, y - 2, 1.4, 1.6, c);
                E(x - 1.2, y - 3.6, 1.4, 1.6, c);
                E(x + 1.2, y - 3.6, 1.4, 1.6, c);
                E(x + 3.4, y - 2, 1.4, 1.6, c);
            }
            else if (s.act == ShamAct.HitFarmer)
            {
                FillRect(x - 1.2, y - 5, 2.4, 6.2, c);
                E(x, y + 3.6, 1.4, 1.4, c);
            }
            else if (s.act == ShamAct.Whirl)
            {
                double a0 = -vt * 7;                              // little spinning swirl
                Arc(x, y, 4.6, a0, a0 + 4.2, 1.6, c);
                Arc(x, y, 2.2, a0 + 1.5, a0 + 5.2, 1.4, c);
            }
            else
            {
                E(x, y + 1, 3.2, 3.6, c);                        // sweat drop
                E(x, y - 2.4, 1.6, 2.2, c);
            }
            if (!bars) return;
            // six bars = how much it wants each option right now (chosen one outlined)
            int na = Shambler.N_ACT;
            double bw = 4.2, gap = 1.3, bh = 22;
            double bx = x - (na * bw + (na - 1) * gap) / 2, by = y - 13;
            FillRect(bx - 2, by - bh - 2, na * bw + (na - 1) * gap + 4, bh + 4, Col(20, 16, 10, .78));
            for (int i = 0; i < na; i++)
            {
                double v = System.Math.Max(0.02, System.Math.Min(1, s.scores[i]));
                double cx = bx + i * (bw + gap);
                FillRect(cx, by - bh, bw, bh, Col(255, 255, 255, .08));
                FillRect(cx, by - bh * v, bw, bh * v, ACT_COL[i]);
                if (i == (int)s.act) FillRect(cx, by + 1, bw, 1.4, Color.white);
            }
        }

        Item MakeItem(double y, int seq, int k, double x, double h, object refObj, string imgName)
        {
            Item it = new Item();
            it.y = y; it.seq = seq; it.k = k; it.x = x; it.h = h; it.refObj = refObj; it.imgName = imgName;
            return it;
        }

    }

    // procedural textures for the canvas shapes
    public static class Shapes
    {
        // white anti-aliased disc, scaled per draw for circles/ellipses
        public static Sprite Circle()
        {
            int N = 256;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, true);
            Color32[] px = new Color32[N * N];
            float rr = N / 2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = x + 0.5f - rr, dy = y + 0.5f - rr;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(rr - d);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255));
                }
            t.SetPixels32(px);
            t.Apply(true);
            t.filterMode = FilterMode.Trilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            Sprite cs = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N, 0, SpriteMeshType.FullRect);
            cs.name = "circle";
            return cs;
        }

        static float SStep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        // white soft disc, alpha from distance: 0 glow, 1 darkness with a clear centre, 2 vignette
        public static Sprite Radial(int N, int mode)
        {
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + .5f) / N * 2 - 1, dy = (y + .5f) / N * 2 - 1;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a;
                    if (mode == 0) { float q = Mathf.Max(0, 1 - d); a = q * q; }
                    else if (mode == 1) a = SStep(0.05f, 0.30f, d);
                    else a = SStep(0.55f, 1.25f, d) * 0.85f;
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255));
                }
            t.SetPixels32(px);
            t.Apply(false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            Sprite rs = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N, 0, SpriteMeshType.FullRect);
            rs.name = "radial" + mode;
            return rs;
        }

        public static Sprite Pixel()
        {
            Texture2D t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(px);
            t.Apply();
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4, 0, SpriteMeshType.FullRect);
        }

        // one grass tuft: 3 strokes (width 2) from the base, drawn as a single path at 50% alpha
        public static Sprite Tuft()
        {
            int SS = 8;                       // texels per canvas pixel
            float minX = -4, maxX = 4, minY = -9, maxY = 1;
            int W = (int)((maxX - minX) * SS), H = (int)((maxY - minY) * SS);
            Texture2D t = new Texture2D(W, H, TextureFormat.RGBA32, true);
            Color32[] px = new Color32[W * H];
            Vector2[] ends = { new Vector2(-3, -7), new Vector2(0, -8), new Vector2(3, -7) };
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    int hits = 0;
                    for (int sj = 0; sj < 2; sj++)
                        for (int si = 0; si < 2; si++)
                        {
                            float cxp = minX + (i + 0.25f + si * 0.5f) / SS;
                            float cyp = minY + (j + 0.25f + sj * 0.5f) / SS;   // j counts down from the top
                            bool inside = false;
                            foreach (Vector2 e in ends)
                                if (OnSegment(new Vector2(cxp, cyp), Vector2.zero, e, 1f)) { inside = true; break; }
                            if (inside) hits++;
                        }
                    byte a = (byte)Mathf.RoundToInt(hits / 4f * 0.5f * 255);
                    int row = H - 1 - j;   // texture rows go bottom-up
                    px[row * W + i] = new Color32(40, 80, 30, a);
                }
            t.SetPixels32(px);
            t.Apply(true);
            t.filterMode = FilterMode.Trilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            // pivot at the tuft base (canvas 0,0)
            Vector2 pivot = new Vector2((0 - minX) / (maxX - minX), (maxY - 0) / (maxY - minY));
            return Sprite.Create(t, new Rect(0, 0, W, H), pivot, SS, 0, SpriteMeshType.FullRect);
        }

        // inside a stroke of half-width hw with butt caps
        static bool OnSegment(Vector2 p, Vector2 a, Vector2 b, float hw)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float u = Vector2.Dot(p - a, ab) / len2;
            if (u < 0 || u > 1) return false;
            Vector2 proj = a + ab * u;
            return (p - proj).sqrMagnitude <= hw * hw;
        }
    }
}
