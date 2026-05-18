namespace XelsCombatAI.Combat;

internal static class GoalZoneScorePolicy
{
    public const float TotalAdvisoryBudget = 0.075f;
    public const float MaxRawScore = 1f;
    public const float RawScoreForFullBudget = 4f;
    public const float StrongPreference = 1f;
    public const float NormalPreference = 0.75f;
    public const float AoeRepositionPreference = 0.5f;
    public const float WeakPreference = 0.25f;
    public const float PackApproachPreference = 0.2f;

    public static float ClampAdvisoryScore(float rawScore)
    {
        return System.Math.Clamp(rawScore / RawScoreForFullBudget, 0f, 1f) * TotalAdvisoryBudget;
    }

    public static float ApplyPriorityWeight(float score, BossModGoalPriority priority, float contributionWeight)
    {
        return score * PriorityWeight(priority) * contributionWeight;
    }

    public static float PriorityWeight(BossModGoalPriority priority)
    {
        return priority switch
        {
            BossModGoalPriority.DefensiveMechanic => 2.5f,
            BossModGoalPriority.ImmediateAction => 2f,
            BossModGoalPriority.PartyUtility => 1.5f,
            BossModGoalPriority.Uptime => 1f,
            BossModGoalPriority.Convenience => 0.5f,
            _ => 1f
        };
    }
}
