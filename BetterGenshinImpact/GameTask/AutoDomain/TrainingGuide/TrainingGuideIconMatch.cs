using System;
using System.Reflection;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoDomain.TrainingGuide;

/// <summary>培养入口专用的 ItemV2 候选读取，不改变公共识别器的阈值和返回值。</summary>
internal static class TrainingGuideIconMatch
{
    private static readonly MethodInfo? MatchMethod = typeof(ItemRecognizer).GetMethod(
        "Match", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Mat) }, null);
    private static readonly FieldInfo? ThresholdField = typeof(ItemRecognizer).GetField(
        "MatchThreshold", BindingFlags.Static | BindingFlags.NonPublic);

    internal sealed record RecognitionResult(string? RecognizedName, (ItemIconCandidate Candidate, double Threshold)? CandidateMatch);

    public static RecognitionResult Recognize(
        IItemIconRecognizer recognizer, Mat icon)
    {
        if (recognizer is not ItemRecognizer) return new RecognitionResult(recognizer.Recognize(icon), null);
        // 同一次推理供原阈值判断、诊断和遮挡补救共同使用。
        if (MatchMethod == null || ThresholdField?.GetRawConstantValue() is not double)
            return new RecognitionResult(recognizer.Recognize(icon), null);
        var match = Read(recognizer, icon);
        return new RecognitionResult(match.Candidate.Score >= match.Threshold ? match.Candidate.Name : null, match);
    }

    public static (ItemIconCandidate Candidate, double Threshold) Read(IItemIconRecognizer recognizer, Mat icon)
    {
        // 仅由培养模块调用，不改变公共识别器的阈值或接口。
        if (recognizer is not ItemRecognizer || MatchMethod == null ||
            ThresholdField?.GetRawConstantValue() is not double threshold)
            throw new InvalidOperationException("无法读取 ItemV2 诊断候选和阈值");
        if (MatchMethod.Invoke(recognizer, new object[] { icon }) is not ItemIconCandidate candidate)
            throw new InvalidOperationException("ItemV2 未返回有效诊断候选");
        return (candidate, threshold);
    }
}
