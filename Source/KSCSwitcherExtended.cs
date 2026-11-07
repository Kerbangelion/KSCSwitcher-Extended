// KSCSwitcher-Extended: lets KSCSwitcher move the Space Center to a body other than the home world.
// Add-on for KSCSwitcher (KSP-RO). Does not modify KSCSwitcher, Kopernicus or the stock game files:
// everything is done at runtime through Harmony patches. Delete the folder to remove it.
//
// A KSCSwitcher site becomes an off-world site by adding   body = <CelestialBody name>   to its Site node.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using regexKSP;
using KSite = regexKSP.LaunchSite;

namespace KSCSwitcherExtended
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Bootstrap : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(this);
            try { Patches.Apply(new Harmony("RedEarth.KSCSwitcherExtended")); }
            catch (Exception e) { Debug.LogError("[KSCSwitcher-Extended] patching failed: " + e); }
        }
    }

    public static class Core
    {
        const string LOG = "[KSCSwitcher-Extended] ";
        const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Body currently hosting the KSC, or null when it is on the home world.</summary>
        public static CelestialBody OffworldBody { get; private set; }
        public static bool SceneReloadPending;

        public static CelestialBody Home
        {
            get
            {
                CelestialBody h = Planetarium.fetch != null ? Planetarium.fetch.Home : null;
                return h != null ? h : FlightGlobals.GetHomeBody();
            }
        }

        /// <summary>Replacement for FlightGlobals.GetHomeBody() inside Kopernicus' KSC camera/floating-origin code.</summary>
        public static CelestialBody KSCHostBody()
        {
            return OffworldBody != null ? OffworldBody : FlightGlobals.GetHomeBody();
        }

        public static CelestialBody SiteBody(ConfigNode site)
        {
            string n = site?.GetValue("body");
            if (string.IsNullOrEmpty(n)) return Home;
            CelestialBody b = FlightGlobals.Bodies.FirstOrDefault(x => x.name == n || x.bodyName == n);
            if (b == null) Debug.LogError(LOG + "unknown body '" + n + "' for site " + site.GetValue("name"));
            return b;
        }

        public static CelestialBody SiteBody(string siteName)
        {
            ConfigNode n = KSCLoader.instance?.Sites?.GetSiteByName(siteName);
            return n == null ? Home : SiteBody(n);
        }

        public static PQSCity FindKSC()
        {
            foreach (CelestialBody b in new[] { OffworldBody, Home })
            {
                Transform t = b?.pqsController?.transform.Find("KSC");
                PQSCity c = t != null ? t.GetComponent<PQSCity>() : null;
                if (c != null) return c;
            }
            return Resources.FindObjectsOfTypeAll<PQSCity>().FirstOrDefault(c => c.name == "KSC" && c.sphere != null);
        }

        static void SetField(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name, ANY);
            if (f == null) { Debug.LogWarning(LOG + "field " + o.GetType().Name + "." + name + " not found"); return; }
            f.SetValue(o, value);
        }

        static T GetField<T>(object o, string name) where T : class
        {
            return o.GetType().GetField(name, ANY)?.GetValue(o) as T;
        }

        static void MoveMod(PQS from, PQS to, PQSMod mod)
        {
            FieldInfo fm = typeof(PQS).GetField("mods", ANY), fc = typeof(PQS).GetField("modCount", ANY);
            if (from != null)
            {
                PQSMod[] a = (PQSMod[])fm.GetValue(from) ?? new PQSMod[0];
                PQSMod[] na = a.Where(m => m != mod).ToArray();
                fm.SetValue(from, na); fc.SetValue(from, na.Length);
            }
            PQSMod[] b = (PQSMod[])fm.GetValue(to) ?? new PQSMod[0];
            if (!b.Contains(mod))
            {
                PQSMod[] nb = b.Concat(new[] { mod }).OrderBy(m => m.order).ToArray();
                fm.SetValue(to, nb); fc.SetValue(to, nb.Length);
            }
            mod.sphere = to;
        }

        /// <summary>Re-hosts the KSC (and everything stock that points at its host body) on 'target'.</summary>
        public static bool MoveKSC(CelestialBody target)
        {
            PQSCity ksc = FindKSC();
            if (ksc == null || target?.pqsController == null) { Debug.LogError(LOG + "cannot find KSC or target PQS"); return false; }
            PQS to = target.pqsController, from = ksc.sphere;
            OffworldBody = target == Home ? null : target;
            if (from == to) return true;

            Debug.Log(LOG + "moving KSC from " + (from != null ? from.name : "?") + " to " + to.name);
            ParallaxFix.ResetKSCGround(ksc);
            // KSCSwitcher re-applies the saved site when a save is loaded, i.e. after the Space Center
            // scene was already set up for the previous body: rebuild the scene once for the new one.
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER) SceneReloadPending = true;

            // 1. surface objects of the KSC: re-register with the new PQS so LOD/visibility updates run there
            foreach (PQSMod m in ksc.GetComponentsInChildren<PQSMod>(true).Where(m => (m is PQSCity || m is PQSCity2) && m.sphere == from).ToList())
                MoveMod(from, to, m);

            // 2. hierarchy
            ksc.transform.SetParent(to.transform, false);
            ksc.ResetCelestialBody();
            if (ksc.celestialBody != target) SetField(ksc, "body", target);

            // 3. the stock scene setup (what SetSpaceCentre / SetEditor / SetTrackingStation activate and focus)
            PSystemSetup ps = PSystemSetup.Instance;
            if (ps != null)
            {
                SetField(ps, "pqs", to);
                SetField(ps, "cb", to.PQSModCBTransform != null ? to.PQSModCBTransform : to.GetComponentInChildren<PQSMod_CelestialBodyTransform>(true));
                SetField(ps, "pqsToActivate", to.gameObject.name);
                Transform sc = GetField<Transform>(ps, "scTransform");
                if (sc == null || !sc.IsChildOf(ksc.transform)) SetField(ps, "scTransform", ksc.transform);

                foreach (PSystemSetup.SpaceCenterFacility f in ps.SpaceCenterFacilities ?? new PSystemSetup.SpaceCenterFacility[0])
                {
                    if (f?.facilityTransform == null || !f.facilityTransform.IsChildOf(ksc.transform)) continue;
                    f.facilityPQS = to; f.hostBody = target; f.pqsName = to.gameObject.name;
                }
            }

            // 4. the KSC's own CommNet ground station(s)
            foreach (CommNet.CommNetHome h in ksc.GetComponentsInChildren<CommNet.CommNetHome>(true))
                SetField(h, "body", target);

            return true;
        }

        static void SyncSpaceCenter(PQSCity ksc)
        {
            SpaceCenter sc = SpaceCenter.Instance;
            if (sc == null) return;
            if (!sc.transform.IsChildOf(ksc.transform)) sc.transform.SetParent(ksc.transform.parent, true);
            sc.transform.position = ksc.transform.position;
            sc.transform.rotation = ksc.transform.rotation;
            sc.Start();
        }

        /// <summary>Places the KSC at an off-world KSCSwitcher site (mirrors KSCSwitcher.SetSite for another body).</summary>
        public static bool SetOffworldSite(ConfigNode site, CelestialBody body)
        {
            ConfigNode p = site.GetNode("PQSCity");
            if (p == null || !MoveKSC(body)) return false;
            PQSCity ksc = FindKSC();

            double.TryParse(p.GetValue("latitude"), out double lat);
            double.TryParse(p.GetValue("longitude"), out double lon);
            double off = 0; double.TryParse(p.GetValue("repositionRadiusOffset"), out off);
            float ang = 0; float.TryParse(p.GetValue("reorientFinalAngle"), out ang);

            ksc.repositionRadial = KSCSwitcher.LLAtoECEF(lat, lon, 0, body.Radius);
            ksc.repositionToSphere = true;
            ksc.repositionToSphereSurface = false;
            ksc.repositionRadiusOffset = off;
            ksc.reorientToSphere = true;
            ksc.reorientFinalAngle = ang;
            if (GrassSeasoner.TryGetKSCGrassColor(body, p, out Color col)) GrassSeasoner.SetGrassColor(col);

            ksc.OnSetup();
            ksc.Orientate(true);     // what PQSCity.OnPostSetup does, without needing a coroutine
            ksc.CheckLocals();
            SyncSpaceCenter(ksc);

            foreach (CommNet.CommNetHome h in ksc.GetComponentsInChildren<CommNet.CommNetHome>(true))
            {
                SetField(h, "lat", lat); SetField(h, "lon", lon); SetField(h, "alt", off + 30.0);
            }

            string name = site.GetValue("name");
            if (KSCLoader.instance != null) KSCLoader.instance.Sites.lastSite = name;
            if (LastKSC.fetch != null) LastKSC.fetch.lastSite = name;
            Debug.Log(LOG + "KSC is now at " + name + " on " + body.name + " (" + lat + ", " + lon + ", +" + off + " m)");
            return true;
        }
    }

    /// <summary>
    /// Parallax Continued re-skins the KSC's lawn/tarmac with a material clone per host body and caches it.
    /// After a trip to another body the KSC keeps that body's clone, whose textures Parallax unloads later,
    /// so the ground shows up pink. Put the original materials back whenever the KSC moves, and make Parallax
    /// re-apply its terrain textures once they are actually loaded in the next scene.
    /// </summary>
    public static class ParallaxFix
    {
        const string LOG = "[KSCSwitcher-Extended] ";
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type sgt = AccessTools.TypeByName("Parallax.Structures.StructureGroundTerrain");
        public static bool Pending;

        public static void ResetKSCGround(PQSCity ksc)
        {
            if (sgt == null || ksc == null) return;
            try
            {
                var clones = sgt.GetField("bodyMaterialClones", S)?.GetValue(null) as System.Collections.IDictionary;
                var renderers = sgt.GetField("groundRenderers", S)?.GetValue(null) as System.Collections.IDictionary;
                var original = new Dictionary<Material, Material>();
                if (clones != null)
                    foreach (System.Collections.DictionaryEntry e in clones)
                    {
                        Material o = e.Key.GetType().GetField("Item1")?.GetValue(e.Key) as Material;
                        if (o != null && e.Value is Material c && c != o) original[c] = o;
                    }
                int n = 0;
                foreach (Renderer r in ksc.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials; bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                        for (int guard = 0; mats[i] != null && guard < 8 && original.TryGetValue(mats[i], out Material o); guard++)
                        { mats[i] = o; changed = true; }
                    if (changed) { r.sharedMaterials = mats; n++; }
                    renderers?.Remove(r);
                }
                Pending = true;
                Debug.Log(LOG + "restored original KSC ground materials on " + n + " renderers (Parallax)");
            }
            catch (Exception e) { Debug.LogWarning(LOG + "Parallax ground reset failed: " + e.Message); }
        }

        // Called every frame by Watcher in SC/editor scenes while a reset is pending.
        public static bool TryReapply()
        {
            if (sgt == null) return true;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return false;
            var bodies = AccessTools.TypeByName("Parallax.ConfigLoader")?.GetField("parallaxTerrainBodies", S)?.GetValue(null) as System.Collections.IDictionary;
            object tb = bodies != null && bodies.Contains(body.name) ? bodies[body.name] : null;
            if (tb == null) return true;                                    // no Parallax terrain on this body
            if (!(bool)(tb.GetType().GetProperty("Loaded")?.GetValue(tb) ?? true)) return false;   // wait for textures
            FieldInfo applied = sgt.GetField("appliedBody", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(sgt)) applied?.SetValue(o, null);   // forces a re-apply next Update
            Debug.Log(LOG + "asked Parallax to re-apply KSC ground textures for " + body.name);
            return true;
        }
    }

    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class Watcher : MonoBehaviour
    {
        float t;
        static readonly FieldInfo fDominant = AccessTools.Field(typeof(OrbitPhysicsManager), "dominantBody");
        static readonly FieldInfo fOpmFetch = AccessTools.Field(typeof(OrbitPhysicsManager), "fetch");

        // In the Space Center the sky turns with the body the KSC stands on (it is the "inverse rotation"
        // body). TiltEm only lets that body drive the sky if it is also OrbitPhysicsManager's dominant
        // body, which stays the home planet outside of flight -> with the KSC on the Moon the stars froze.
        static void SyncDominantBody()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER || fDominant == null || fOpmFetch == null) return;
            var opm = fOpmFetch.GetValue(null) as OrbitPhysicsManager;
            if (opm == null) return;
            CelestialBody kb = Core.KSCHostBody();
            if (kb == null || !kb.inverseRotation) return;
            if ((fDominant.GetValue(opm) as CelestialBody) == kb) return;
            fDominant.SetValue(opm, kb);
            Debug.Log("[KSCSwitcher-Extended] sky frame: dominant body set to " + kb.bodyName + " for the Space Center");
        }

        // The Space Center makes the KSC's body the "reverse" body (it stays put, the universe moves
        // around it), but stock only flags that one body. Planetarium.FindRootBody walks down from the Sun
        // through reverse-flagged bodies only, so with the KSC on a moon the Moon was never reached:
        // no rotation, no orbit updates, frozen sky and a constant local time. In flight the stock code
        // flags the whole chain up to the Sun (OrbitPhysicsManager.SetReverseOrbit); do the same here.
        static void EnsureReverseChain()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) return;
            CelestialBody kb = Core.KSCHostBody();
            if (kb == null || !kb.inverseRotation || kb.orbitDriver == null || !kb.orbitDriver.reverse) return;
            int n = 0;
            for (CelestialBody b = kb.referenceBody; b != null && b.orbitDriver != null && n < 8; b = b.referenceBody)
            {
                if (!b.orbitDriver.reverse) { b.orbitDriver.reverse = true; n++; }
                if (b.referenceBody == b) break;
            }
            if (n > 0) Debug.Log("[KSCSwitcher-Extended] sky frame: reverse-orbit chain set from " + kb.bodyName + " up to the star (" + n + " parent(s))");
        }

        void Update()
        {
            try { EnsureReverseChain(); } catch (Exception e) { Debug.LogWarning("[KSCSwitcher-Extended] reverse chain failed: " + e.Message); }
            try { SyncDominantBody(); } catch (Exception e) { Debug.LogWarning("[KSCSwitcher-Extended] sky frame sync failed: " + e.Message); }
            if (Core.SceneReloadPending && HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                if ((t += Time.unscaledDeltaTime) < 0.2f) return;
                t = 0; Core.SceneReloadPending = false;
                Debug.Log("[KSCSwitcher-Extended] KSC changed body after the Space Center was set up: reloading the Space Center");
                HighLogic.LoadScene(GameScenes.SPACECENTER);
                return;
            }
            Core.SceneReloadPending = false;
            if (!ParallaxFix.Pending) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && !HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;
            t += Time.unscaledDeltaTime;
            if (t < 1f) return;                       // let Parallax's own scene start run first
            t = 0;
            if (ParallaxFix.TryReapply()) ParallaxFix.Pending = false;
        }
    }

    public static class Patches
    {
        const string LOG = "[KSCSwitcher-Extended] ";
        static FieldInfo fSites, fLsTex, fLsActive, fShowSites, fActive, fSiteText;
        static MethodInfo mSetSiteLS;

        public static void Apply(Harmony h)
        {
            Type ks = typeof(KSCSwitcher);
            h.Patch(AccessTools.Method(ks, "SetSite", new[] { typeof(ConfigNode) }), prefix: new HarmonyMethod(typeof(Patches), nameof(SetSitePrefix)));
            h.Patch(AccessTools.Method(ks, "OnDraw"), prefix: new HarmonyMethod(typeof(Patches), nameof(OnDrawPrefix)));
            h.Patch(AccessTools.Method(ks, "FocusOnSite"), prefix: new HarmonyMethod(typeof(Patches), nameof(FocusPrefix)));

            fSites = AccessTools.Field(ks, "siteLocations"); fLsTex = AccessTools.Field(ks, "lsTexture");
            fLsActive = AccessTools.Field(ks, "lsActiveTexture"); fShowSites = AccessTools.Field(ks, "showSites");
            fActive = AccessTools.Field(ks, "activeSite"); fSiteText = AccessTools.Field(ks, "siteText");
            mSetSiteLS = AccessTools.Method(ks, "SetSite", new[] { typeof(KSite) });

            // Kopernicus hard-codes FlightGlobals.GetHomeBody() when it looks the KSC up for the Space Center
            // camera and the floating origin: point those lookups at wherever the KSC currently is.
            var trans = new HarmonyMethod(typeof(Patches), nameof(HomeBodyToKSCBody));
            foreach (string t in new[] { "Kopernicus.Patches.SpaceCenterCamera2_Start:Prefix",
                                         "Kopernicus.RuntimeUtility.PreciseKSCFloatingOrigin:SetFloatingOriginToKSC" })
            {
                string[] s = t.Split(':');
                MethodInfo m = AccessTools.TypeByName(s[0]) != null ? AccessTools.Method(AccessTools.TypeByName(s[0]), s[1]) : null;
                if (m == null) { Debug.Log(LOG + t + " not found (no Kopernicus?), skipped"); continue; }
                h.Patch(m, transpiler: trans);
            }
            // Stock "warp to next morning" computes the solar day from the body's own orbital period,
            // which is wrong for a moon (tidally locked: rotation == orbit => infinite day). Use the
            // period of the ancestor that orbits the star instead (Earth's year for the Moon).
            var day = new HarmonyMethod(typeof(Patches), nameof(SolarDayFix));
            Type wm = AccessTools.TypeByName("KSP.UI.UIWarpToNextMorning");
            if (wm != null)
                foreach (MethodInfo m in wm.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "Setup" || m.Name == "CalculateTimeToDaylightFromVessel") h.Patch(m, transpiler: day);
            Debug.Log(LOG + "patches applied");
        }

        public static double StarOrbitPeriod(CelestialBody b)
        {
            CelestialBody c = b;
            for (int i = 0; i < 8 && c != null && c.orbit != null && c.referenceBody != null && c.referenceBody != c
                            && c.referenceBody.orbit != null && c.referenceBody.referenceBody != c.referenceBody; i++)
                c = c.referenceBody;                       // climb until the parent is the star
            return c != null && c.orbit != null ? c.orbit.period : (b?.orbit != null ? b.orbit.period : 0);
        }

        static IEnumerable<CodeInstruction> SolarDayFix(IEnumerable<CodeInstruction> ins)
        {
            MethodInfo getOrbit = AccessTools.PropertyGetter(typeof(CelestialBody), "orbit");
            FieldInfo period = AccessTools.Field(typeof(Orbit), "period");
            MethodInfo rep = AccessTools.Method(typeof(Patches), nameof(StarOrbitPeriod));
            List<CodeInstruction> l = ins.ToList(); int n = 0;
            for (int i = 0; i + 1 < l.Count; i++)
            {
                MethodInfo mi = l[i].operand as MethodInfo;
                bool isGet = (l[i].opcode == OpCodes.Call || l[i].opcode == OpCodes.Callvirt) && mi != null
                             && (mi == getOrbit || (mi.Name == "get_orbit" && mi.DeclaringType == typeof(CelestialBody)));
                if (!isGet) continue;
                int j = i + 1;                                   // KSP's IL has junk dup/pop pairs after calls
                while (j < l.Count && (l[j].opcode == OpCodes.Dup || l[j].opcode == OpCodes.Pop || l[j].opcode == OpCodes.Nop)) j++;
                FieldInfo fi = j < l.Count ? l[j].operand as FieldInfo : null;
                if (j >= l.Count || l[j].opcode != OpCodes.Ldfld || fi == null || !(fi == period || (fi.Name == "period" && fi.DeclaringType == typeof(Orbit)))) continue;
                l[i].opcode = OpCodes.Call; l[i].operand = rep;  // body -> double (dup/pop on a double are harmless)
                l[j].opcode = OpCodes.Nop; l[j].operand = null; n++;
            }
            Debug.Log(LOG + "warp-to-morning: patched " + n + " orbital-period lookups");
            return l;
        }

        static IEnumerable<CodeInstruction> HomeBodyToKSCBody(IEnumerable<CodeInstruction> ins)
        {
            MethodInfo gh = AccessTools.Method(typeof(FlightGlobals), nameof(FlightGlobals.GetHomeBody));
            MethodInfo rep = AccessTools.Method(typeof(Core), nameof(Core.KSCHostBody));
            foreach (CodeInstruction i in ins)
            {
                if (i.Calls(gh)) { i.opcode = OpCodes.Call; i.operand = rep; }
                yield return i;
            }
        }

        // KSCSwitcher.SetSite(ConfigNode) is used by the site list, the map icons and the save loader.
        static bool SetSitePrefix(ConfigNode __0, ref bool __result)
        {
            try
            {
                CelestialBody body = Core.SiteBody(__0);
                if (body == null) { __result = false; return false; }
                if (body == Core.Home)
                {
                    if (Core.OffworldBody != null) Core.MoveKSC(Core.Home);   // bring it home, then let KSCSwitcher place it
                    return true;
                }
                __result = Core.SetOffworldSite(__0, body);
                return false;
            }
            catch (Exception e) { Debug.LogError(LOG + e); __result = false; return false; }
        }

        // Map icons: draw each site on its own body (KSCSwitcher only knows the home world).
        static bool OnDrawPrefix(KSCSwitcher __instance)
        {
            var sites = fSites.GetValue(__instance) as SortedList<string, KSite>;
            Texture2D tex = fLsTex.GetValue(__instance) as Texture2D, act = fLsActive.GetValue(__instance) as Texture2D;
            if (sites == null || sites.Count < 1 || tex == null || !(bool)fShowSites.GetValue(__instance)) return false;
            CelestialBody target = MapView.MapCamera?.target?.celestialBody;
            if (target == null || MapView.MapCamera.Distance >= target.Radius / 1000) return false;

            string activeSite = fActive.GetValue(__instance) as string;
            GUIStyle siteText = fSiteText.GetValue(__instance) as GUIStyle;
            Camera camera = PlanetariumCamera.Camera;
            Vector3d camPos = ScaledSpace.ScaledToLocalSpace(camera.transform.position);
            foreach (KeyValuePair<string, KSite> kvp in sites)
            {
                if (Core.SiteBody(kvp.Key) != target) continue;
                Vector3d point = target.GetWorldSurfacePosition(kvp.Value.geographicLocation.x, kvp.Value.geographicLocation.y, 0);
                if (Vector3d.Angle(camPos - point, target.position - point) <= 90) continue;   // behind the planet
                bool isActive = kvp.Value.name.Equals(activeSite);
                point = camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(point));
                if (point.z < 0) continue;
                Rect r = new Rect((float)point.x, (float)(Screen.height - point.y), 28f, 28f);
                Graphics.DrawTexture(r, isActive && act != null ? act : tex);
                if (r.Contains(Event.current.mousePosition))
                {
                    GUI.Label(new Rect((float)point.x + 28f, (float)(Screen.height - point.y) + 5f, 50, 20), kvp.Value.displayName, siteText);
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                    {
                        if (isActive) ScreenMessages.PostScreenMessage("Cannot set launch site to active site.", 2.5f, ScreenMessageStyle.LOWER_CENTER);
                        else mSetSiteLS.Invoke(__instance, new object[] { kvp.Value });
                    }
                }
            }
            return false;
        }

        // Magnifier button: focus the map on the site's own body.
        static bool FocusPrefix(KSCSwitcher __instance, Vector2d __0)
        {
            var sites = fSites.GetValue(__instance) as SortedList<string, KSite>;
            KSite s = sites?.Values.FirstOrDefault(v => v.geographicLocation.x == __0.x && v.geographicLocation.y == __0.y);
            CelestialBody body = s != null ? Core.SiteBody(s.name) : null;
            if (body == null || body == Core.Home) return true;

            PlanetariumCamera cam = PlanetariumCamera.fetch;
            cam.SetTarget(body.MapObject);
            Vector3d point = ScaledSpace.LocalToScaledSpace(body.GetWorldSurfacePosition(__0.x, __0.y, 0));
            Vector3 vec = ScaledSpace.LocalToScaledSpace(body.transform.localPosition);
            point = (point - vec).normalized * body.Radius;
            cam.SetCamCoordsFromPosition(new Vector3((float)point.x, (float)point.y, (float)point.z));
            cam.SetDistance((float)(body.Radius * 0.0009));
            return false;
        }
    }
}
