using System.IO;
using System.Linq;
using System.Reflection;
using CharacterFactory.Core;
using CharacterFactory.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CharacterFactory.Tests
{
    public class ContractTests
    {
        [Test]
        public void FinalV0DtosDeserializeLiveShapes()
        {
            const string listJson = "[{\"id\":\"0123456789abcdef\",\"name\":\"hero\"," +
                "\"artifact\":{\"available\":true,\"revision\":3,\"bytes\":9346460," +
                "\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
                "\"built_at\":\"2026-08-26T19:33:40-07:00\"},\"latest_job\":null," +
                "\"creation\":{\"requested_interpreter\":\"luna\",\"actual_interpreter\":\"luna\"," +
                "\"warnings\":[{\"code\":\"interpretation_note\"," +
                "\"message\":\"neutral eye default\"}]},\"created_at\":\"now\",\"updated_at\":\"now\"}]";
            var records = JsonConvert.DeserializeObject<CharacterRecord[]>(listJson);
            Assert.That(records, Has.Length.EqualTo(1));
            Assert.That(records[0].IsAvailable, Is.True);
            Assert.That(records[0].Artifact.Revision, Is.EqualTo(3));
            Assert.That(records[0].Creation.Warnings.Single().Code, Is.EqualTo("interpretation_note"));
            var documentedStringWarning = JsonConvert.DeserializeObject<ApiWarning>("\"legacy warning\"");
            Assert.That(documentedStringWarning.Message, Is.EqualTo("legacy warning"));

            const string jobJson = "{\"id\":\"job\",\"operation\":\"create\",\"status\":\"succeeded\"," +
                "\"stage\":\"complete\",\"progress\":1,\"warnings\":[]," +
                "\"result\":{\"character_id\":\"0123456789abcdef\",\"revision\":1}," +
                "\"created_at\":\"now\",\"updated_at\":\"now\"}";
            var job = JsonConvert.DeserializeObject<CharacterJob>(jobJson);
            Assert.That(job.IsSucceeded, Is.True);
            Assert.That(job.Result.CharacterId, Is.EqualTo("0123456789abcdef"));
        }

        [Test]
        public void MutationRequestsMatchTheStrictLiveV0Schema()
        {
            var create = JObject.Parse(JsonConvert.SerializeObject(new CreateCharacterRequest
            {
                Prompt = "a lighthouse keeper",
                Turbo = false,
            }));
            CollectionAssert.AreEquivalent(new[] { "prompt", "turbo" },
                create.Properties().Select(p => p.Name));

            var seeded = JObject.Parse(JsonConvert.SerializeObject(new CreateCharacterRequest
            {
                Prompt = "a lighthouse keeper",
                Seed = 42,
            }));
            Assert.That((long)seeded["seed"], Is.EqualTo(42));

            var rebuild = JObject.Parse(JsonConvert.SerializeObject(new RebuildCharacterRequest
            {
                From = "assemble",
                Turbo = true,
            }));
            CollectionAssert.AreEquivalent(new[] { "from", "turbo" },
                rebuild.Properties().Select(p => p.Name));
        }

        [Test]
        public void Manifest06ParsesAndEnforcesMandatoryBaseline()
        {
            var json = BuildManifest();
            var manifest = ExportManifest.FromJson(json);
            Assert.DoesNotThrow(manifest.RequireSupportedBaseline);
            Assert.That(manifest.SchemaVersion, Is.EqualTo("0.6"));
            Assert.That(manifest.HumanoidMap, Has.Count.EqualTo(54));
            Assert.That(manifest.Morphs.Names, Has.Count.EqualTo(72));
            Assert.That(manifest.Grounding.RootSceneHeightM, Is.EqualTo(0.924f).Within(0.00001f));
            Assert.That(manifest.Limitations.Entries, Has.Count.EqualTo(2));
            Assert.That(manifest.Limitations.MorphCases().Count(), Is.EqualTo(1));
            Assert.That(manifest.Limitations.Entries.Any(e => e.Kind == "neutral-seating"), Is.True);

            json["schema_version"] = "0.7";
            Assert.Throws<System.IO.InvalidDataException>(
                () => ExportManifest.FromJson(json).RequireSupportedBaseline());
        }

        [Test]
        public void ReferenceMotionsAreFullBodyHumanoidAndDoNotOwnTheFace()
        {
            var motions = ReferenceMotionLibrary.Load();
            var required = new[]
            {
                "Left Upper Leg Front-Back", "Right Upper Leg Front-Back",
                "Left Lower Leg Stretch", "Right Lower Leg Stretch",
                "Left Foot Up-Down", "Right Foot Up-Down",
                "Spine Front-Back", "Chest Front-Back",
                "Left Arm Front-Back", "Right Arm Front-Back",
            };
            foreach (var clip in motions.All)
            {
                Assert.That(clip.isHumanMotion, Is.True, clip.name);
                var properties = AnimationUtility.GetCurveBindings(clip)
                    .Select(b => b.propertyName).ToArray();
                Assert.That(properties, Is.SupersetOf(required), clip.name);
                Assert.That(properties.Any(p => p.StartsWith("blendShape.")
                    || p == "Jaw Close" || p.StartsWith("Left Eye") || p.StartsWith("Right Eye")),
                    Is.False, clip.name + " must leave facial playback independent");
            }
        }

        [Test]
        public void ReferenceDriverUsesStableControllerParameters()
        {
            Assert.That(ReferenceMotionDriver.MoveSpeedParameter, Is.EqualTo("MoveSpeed"));
            Assert.That(ReferenceMotionDriver.TalkingParameter, Is.EqualTo("Talking"));
            Assert.That(ReferenceMotionDriver.InteractParameter, Is.EqualTo("Interact"));
        }

        [Test]
        public void ManifestJawUnitNameComposesMorphAndJointWhileSetMorphRemainsRaw()
        {
            var root = new GameObject("Jaw composition test");
            var jaw = new GameObject("c_jaw").transform;
            jaw.SetParent(root.transform, false);
            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "Jaw composition test mesh" };
            mesh.vertices = new[] { Vector3.zero };
            var zero = new[] { Vector3.zero };
            var delta = new[] { new Vector3(0f, .01f, 0f) };
            for (int i = 0; i <= 24; i++)
                mesh.AddBlendShapeFrame($"facs_{i:00}", 1f, i == 24 ? delta : zero, zero, zero);
            renderer.sharedMesh = mesh;
            var controller = root.AddComponent<ExpressionController>();
            controller.Body = renderer;
            controller.JawJoint = jaw;
            controller.JawAxisLocal = Vector3.forward;
            controller.JawFitAngleDegrees = 15f;
            controller.JawFitMorphIndex = 24;
            var rest = jaw.localRotation;
            try
            {
                controller.SetExpression("facs_24", .6f);
                Assert.That(controller.GetMorph(24), Is.EqualTo(.6f).Within(.0001f));
                Assert.That(controller.JawOpen, Is.EqualTo(.6f).Within(.0001f));
                Assert.That(Quaternion.Angle(rest, jaw.localRotation), Is.EqualTo(9f).Within(.01f));

                controller.SetMorph(24, .2f);
                Assert.That(controller.GetMorph(24), Is.EqualTo(.2f).Within(.0001f));
                Assert.That(controller.JawOpen, Is.EqualTo(.6f).Within(.0001f),
                    "SetMorph is the explicit morph-only escape hatch.");
                Assert.That(Quaternion.Angle(rest, jaw.localRotation), Is.EqualTo(9f).Within(.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SemanticExpressionNamesInitializeLazilyForEditModeSampling()
        {
            var root = new GameObject("Lazy expression lookup test");
            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "Lazy expression lookup mesh" };
            mesh.vertices = new[] { Vector3.zero };
            var zero = new[] { Vector3.zero };
            var delta = new[] { new Vector3(0f, .01f, 0f) };
            for (int i = 0; i <= 15; i++)
                mesh.AddBlendShapeFrame($"facs_{i:00}", 1f, i == 14 ? delta : zero, zero, zero);
            renderer.sharedMesh = mesh;
            var controller = root.AddComponent<ExpressionController>();
            controller.Body = renderer;
            // Add this after AddComponent so neither Awake nor OnValidate can populate the
            // non-serialized lookup for us. This is the consumer editor-script lifecycle path.
            controller.Semantics.Add(new ExpressionController.SemanticEntry
                { Name = "blink_left", Index = 14 });
            try
            {
                Assert.DoesNotThrow(() => controller.SetExpression("blink_left", .65f));
                Assert.That(controller.GetMorph(14), Is.EqualTo(.65f).Within(.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PersistedGeneratedAssetMainObjectMatchesFilenameOnCreateAndUpdate()
        {
            const string folder = "Assets/Temp/Tests";
            const string path = folder + "/scene_Blink.anim";
            Directory.CreateDirectory(Path.GetFullPath(folder));
            AssetDatabase.Refresh();
            var method = typeof(CharacterImportPipeline).GetMethod("SaveOrUpdate",
                BindingFlags.Static | BindingFlags.NonPublic)?.MakeGenericMethod(typeof(AnimationClip));
            Assert.That(method, Is.Not.Null);
            try
            {
                var created = (AnimationClip)method.Invoke(null,
                    new object[] { new AnimationClip { name = "CF_Blink" }, path });
                Assert.That(created.name, Is.EqualTo("scene_Blink"));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path).name, Is.EqualTo("scene_Blink"));

                var updated = (AnimationClip)method.Invoke(null,
                    new object[] { new AnimationClip { name = "CF_Blink" }, path });
                Assert.That(updated.name, Is.EqualTo("scene_Blink"));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path).name, Is.EqualTo("scene_Blink"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void AuthoritativeSomaBodyanimBakesOneReusableFacialFreeHumanoidClip()
        {
            string source = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../MotionCandidates/Kimodo/idle-v3.bodyanim.json"));
            if (!File.Exists(source))
                Assert.Ignore("The optional SOMA integration fixture is not present in this project.");

            const string output = "Assets/Temp/Tests/CF_BodyanimContractTest.anim";
            try
            {
                var inspected = BodyAnimationImporter.Inspect(source);
                Assert.That(inspected.SkeletonId, Is.EqualTo(BodyAnimationImporter.SomaSkeletonId));
                Assert.That(inspected.SkeletonDefinitionId, Is.EqualTo(BodyAnimationImporter.SomaDefinitionId));
                Assert.That(inspected.SkeletonDefinitionSha256,
                    Is.EqualTo(BodyAnimationImporter.SomaDefinitionSha256));
                Assert.That(inspected.RootMotionPolicy, Is.EqualTo("in_place"));
                Assert.That(inspected.FrameCount, Is.EqualTo(120));
                Assert.That(inspected.DurationSeconds, Is.EqualTo(4f).Within(.0001f));

                var imported = BodyAnimationImporter.Import(source, output,
                    new BodyAnimationImporter.Options { ClipName = "CF_BodyanimContractTest", Loop = true });
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(output);
                Assert.That(clip, Is.Not.Null);
                Assert.That(clip.isHumanMotion, Is.True);
                Assert.That(clip.length, Is.EqualTo(4f).Within(.001f));
                Assert.That(imported.HumanoidBindings, Is.GreaterThan(90));
                Assert.That(imported.FacialBindings, Is.Zero);

                var metadata = AssetDatabase.LoadAllAssetsAtPath(output)
                    .OfType<BodyAnimationMetadata>().Single();
                Assert.That(metadata.SkeletonDefinitionSha256,
                    Is.EqualTo(BodyAnimationImporter.SomaDefinitionSha256));
                Assert.That(metadata.RootMotionPolicy, Is.EqualTo("in_place"));
                Assert.That(metadata.FootContactChannels, Has.Length.EqualTo(6));
                Assert.That(metadata.FootContacts, Has.Length.EqualTo(120));
                Assert.That(metadata.ImportedAsLoop, Is.True);

                var curve = AnimationUtility.GetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve("", typeof(Animator), "Left Upper Leg Front-Back"));
                Assert.That(curve, Is.Not.Null);
                Assert.That(curve.Evaluate(clip.length), Is.EqualTo(curve.Evaluate(0f)).Within(.0001f),
                    "A looped bodyanim clip must author the first sample at the exact next-cycle boundary.");
            }
            finally
            {
                AssetDatabase.DeleteAsset(output);
            }
        }

        [Test]
        public void GeneratedReferenceLibraryCarriesAuthoredProvenanceAndConditionedLoops()
        {
            var motions = ReferenceMotionLibrary.Load();
            foreach (var clip in motions.All)
            {
                var path = AssetDatabase.GetAssetPath(clip);
                var metadata = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<BodyAnimationMetadata>().Single();
                bool expectedLoop = clip != motions.Interact;
                Assert.That(clip.isHumanMotion, Is.True, clip.name);
                Assert.That(metadata.Provider, Is.EqualTo("kimodo"), clip.name);
                Assert.That(metadata.SkeletonDefinitionSha256,
                    Is.EqualTo(BodyAnimationImporter.SomaDefinitionSha256), clip.name);
                Assert.That(AnimationUtility.GetAnimationClipSettings(clip).loopTime,
                    Is.EqualTo(expectedLoop), clip.name);
                Assert.That(metadata.ImportedAsLoop, Is.EqualTo(expectedLoop), clip.name);
                Assert.That(metadata.LoopConditioned, Is.EqualTo(expectedLoop), clip.name);
                if (!expectedLoop) continue;

                Assert.That(metadata.BoundaryMaximumDegrees, Is.LessThanOrEqualTo(16f), clip.name);
                Assert.That(metadata.BoundaryVelocityMaximumDegreesPerFrame,
                    Is.LessThanOrEqualTo(5f), clip.name);
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    Assert.That(curve.Evaluate(clip.length), Is.EqualTo(curve.Evaluate(0f)).Within(.0001f),
                        $"{clip.name}: {binding.propertyName}");
                }
            }
        }

        static JObject BuildManifest()
        {
            var names = new JArray(Enumerable.Range(0, 72).Select(i => $"facs_{i:00}"));
            var map = new JObject();
            for (int i = 0; i < 54; i++) map[$"Role{i}"] = $"bone_{i}";
            return new JObject
            {
                ["format"] = ExportManifest.ExpectedFormat,
                ["schema_version"] = ExportManifest.SupportedSchemaVersion,
                ["$schema"] = "/v0/schemas/export-manifest-0.6.json",
                ["units"] = "meters",
                ["up_axis"] = "+Y",
                ["forward_axis"] = "+Z",
                ["topology"] = ExportManifest.TopologyMouthInterior,
                ["humanoid_map"] = new JObject { ["convention"] = "unity-humanoid", ["map"] = map },
                ["expression_morphs"] = new JObject { ["count"] = 72, ["names"] = names },
                ["jaw"] = new JObject
                {
                    ["joint"] = "c_jaw",
                    ["rotation_axis_local"] = new JArray(0.019f, 0.007f, 0.999f),
                    ["full_open_degrees"] = 27f,
                    ["expression_unit"] = 24,
                    ["expression_fit_angle_degrees"] = 15f,
                },
                ["grounding"] = new JObject
                {
                    ["coordinate_space"] = "scene",
                    ["up_axis"] = "+Y",
                    ["plane_height_m"] = -0.003f,
                    ["root_joint"] = "root",
                    ["root_offset_to_ground_m"] = 0.927f,
                    ["idle_ground_tolerance_m"] = 0.01f,
                },
                ["animation_limitations"] = new JObject
                {
                    ["entries"] = new JArray
                    {
                        new JObject
                        {
                            ["kind"] = "socket-clearance",
                            ["case"] = "human label",
                            ["params"] = new JObject
                            {
                                ["facs_24"] = 0.25f,
                                ["unit"] = 44,
                                ["weight"] = 0.75f,
                            },
                        },
                        new JObject
                        {
                            ["kind"] = "neutral-seating",
                            ["case"] = "neutral",
                            ["params"] = new JObject { ["facs_24"] = 0f },
                        },
                    },
                },
            };
        }
    }
}
