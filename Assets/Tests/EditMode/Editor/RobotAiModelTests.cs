using NUnit.Framework;
using UnityEngine;
using VRInteraction.AI;
using VRInteraction.AI.Speech;

public class RobotAiModelTests
{
    [Test]
    public void Vec3RoundTripPreservesCoordinates()
    {
        var v = new Vector3(1.25f, -2.5f, 3.75f);
        float[] packed = AiModelUtil.Vec3(v);
        Vector3 unpacked = AiModelUtil.ToVector3(packed);

        Assert.AreEqual(v.x, unpacked.x);
        Assert.AreEqual(v.y, unpacked.y);
        Assert.AreEqual(v.z, unpacked.z);
    }

    [Test]
    public void ToVector3ReturnsZeroForShortArrays()
    {
        Assert.AreEqual(Vector3.zero, AiModelUtil.ToVector3(new[] { 1f, 2f }));
        Assert.AreEqual(Vector3.zero, AiModelUtil.ToVector3(null));
    }

    [Test]
    public void IsFiniteRejectsNanAndInfinity()
    {
        Assert.IsTrue(AiModelUtil.IsFinite(new Vector3(1f, 2f, 3f)));
        Assert.IsFalse(AiModelUtil.IsFinite(new Vector3(float.NaN, 2f, 3f)));
        Assert.IsFalse(AiModelUtil.IsFinite(new Vector3(1f, float.PositiveInfinity, 3f)));
    }

    [Test]
    public void CommandResponseJsonRoundTripKeepsPlanKind()
    {
        var response = new AiCommandResponse
        {
            spoken_reply = "ready",
            plan_ir = new AiPlanIr
            {
                kind = "manipulator_reach",
                robot_id = "UR3_1",
                waypoints = new[]
                {
                    new AiWaypoint { position_m = new[] { 0.1f, 0.2f, 0.3f } }
                }
            }
        };

        string json = JsonUtility.ToJson(response);
        var parsed = JsonUtility.FromJson<AiCommandResponse>(json);

        Assert.AreEqual("manipulator_reach", parsed.plan_ir.kind);
        Assert.AreEqual("UR3_1", parsed.plan_ir.robot_id);
        Assert.AreEqual(1, parsed.plan_ir.waypoints.Length);
    }

    [Test]
    public void ValidatorShapeRejectsContactPlan()
    {
        var validator = new AiPlanValidator();
        var response = ValidShape();
        response.plan_ir.contact_allowed = true;

        Assert.IsFalse(validator.ValidateResponseShape(response, out string error));
        StringAssert.Contains("Contact", error);
    }

    [Test]
    public void ValidatorShapeRejectsLowGroundingConfidence()
    {
        var validator = new AiPlanValidator();
        var response = ValidShape();
        response.visual_grounding = new AiVisualGrounding { confidence = 0.2f };

        Assert.IsFalse(validator.ValidateResponseShape(response, out string error));
        StringAssert.Contains("confidence", error);
    }

    [Test]
    public void ValidatorShapeRejectsIncompleteWaypoint()
    {
        var validator = new AiPlanValidator();
        var response = ValidShape();
        response.plan_ir.waypoints[0].position_m = new[] { 0.1f, 0.2f };

        Assert.IsFalse(validator.ValidateResponseShape(response, out string error));
        StringAssert.Contains("incomplete", error);
    }

    [Test]
    public void ValidatorShapeAcceptsValidWaypoint()
    {
        var validator = new AiPlanValidator();

        Assert.IsTrue(validator.ValidateResponseShape(ValidShape(), out string error));
        Assert.IsNull(error);
    }

    [Test]
    public void WavEncoderWritesRiffWaveHeader()
    {
        byte[] wav = WavEncoder.EncodeMono16(new[] { 0f, 0.5f, -0.5f }, 16000);

        Assert.AreEqual((byte)'R', wav[0]);
        Assert.AreEqual((byte)'I', wav[1]);
        Assert.AreEqual((byte)'F', wav[2]);
        Assert.AreEqual((byte)'F', wav[3]);
        Assert.AreEqual((byte)'W', wav[8]);
        Assert.AreEqual((byte)'A', wav[9]);
        Assert.AreEqual((byte)'V', wav[10]);
        Assert.AreEqual((byte)'E', wav[11]);
        Assert.AreEqual(44 + 3 * 2, wav.Length);
    }

    [Test]
    public void WavDecoderReadsMono16GeneratedByEncoder()
    {
        byte[] wav = WavEncoder.EncodeMono16(new[] { 0f, 0.5f, -0.5f }, 16000);

        Assert.IsTrue(WavDecoder.TryDecodeMono16(wav, out float[] samples,
            out int sampleRateHz, out string error), error);
        Assert.AreEqual(16000, sampleRateHz);
        Assert.AreEqual(3, samples.Length);
        Assert.AreEqual(0f, samples[0], 0.001f);
        Assert.AreEqual(0.5f, samples[1], 0.001f);
        Assert.AreEqual(-0.5f, samples[2], 0.001f);
    }

    [Test]
    public void RecommendedSpeechBundleUsesQuestAsrConstraints()
    {
        var bundle = new AiSpeechModelBundle();
        string[] assets = bundle.RequiredStreamingAssetRelativePaths();

        Assert.AreEqual(AiSpeechModelBundle.RecommendedAsrBundleId,
            bundle.bundle_id);
        Assert.AreEqual(AiSpeechModelBundle.RecommendedAsrModel,
            bundle.source_model);
        Assert.AreEqual(AiSpeechModelBundle.RequiredSampleRateHz,
            bundle.sample_rate_hz);
        CollectionAssert.Contains(assets,
            "ASR/whisper-tiny-en/LogMelSpectro.sentis");
        CollectionAssert.Contains(assets,
            "ASR/whisper-tiny-en/AudioEncoder_Tiny.sentis");
        CollectionAssert.Contains(assets,
            "ASR/whisper-tiny-en/AudioDecoder_Tiny.sentis");
        CollectionAssert.Contains(assets,
            "ASR/whisper-tiny-en/vocab.json");
    }

    private static AiCommandResponse ValidShape()
    {
        return new AiCommandResponse
        {
            visual_grounding = new AiVisualGrounding { confidence = 0.9f },
            plan_ir = new AiPlanIr
            {
                kind = "manipulator_reach",
                robot_id = "UR3_1",
                contact_allowed = false,
                waypoints = new[]
                {
                    new AiWaypoint { position_m = new[] { 0.1f, 0.2f, 0.3f } }
                }
            }
        };
    }
}
