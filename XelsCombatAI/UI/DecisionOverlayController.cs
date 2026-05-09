using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.UI;

internal sealed class DecisionOverlayController(
    Configuration config,
    DalamudServices services,
    AoePackPositioningController aoePackPositioningController,
    PassageOfArmsPositioningController passageOfArmsPositioningController,
    PartyGravityPositioningController partyGravityPositioningController,
    HealerAoePositioningController healerAoePositioningController,
    SurvivabilityZonePositioningController survivabilityZonePositioningController,
    BossModReflectionSafety bossModSafety,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RotationSolverActionReflection rotationSolverActions)
{

    private DecisionOverlayState gapCloserDisplayedState = DecisionOverlayState.Suppressed;
    private DecisionOverlayState gapCloserPendingState = DecisionOverlayState.Suppressed;
    private DateTime gapCloserPendingStateAt = DateTime.MinValue;
    private static readonly TimeSpan GapCloserStateDebounce = TimeSpan.FromMilliseconds(400);

    public void Draw()
    {
        if (!config.Enabled || !config.ShowDecisionOverlay || !services.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var snapshots = this.BuildSnapshots(player).OrderBy(snapshot => snapshot.Priority).ToArray();
        // Group snapshots by their label anchor position so overlapping labels are stacked vertically.
        var anchorCounts = new Dictionary<(int, int), int>();
        foreach (var snapshot in snapshots)
        {
            var anchor = snapshot.Markers.FirstOrDefault()?.Position ??
                         snapshot.Shapes.FirstOrDefault()?.Origin ??
                         snapshot.Lines.FirstOrDefault()?.To;
            int stackIndex = 0;
            if (anchor.HasValue)
            {
                var key = ((int)MathF.Round(anchor.Value.X * 2f), (int)MathF.Round(anchor.Value.Z * 2f));
                stackIndex = anchorCounts.TryGetValue(key, out var count) ? count : 0;
                anchorCounts[key] = stackIndex + 1;
            }
            this.DrawSnapshot(drawList, snapshot, stackIndex);
        }
    }

    private IEnumerable<DecisionOverlaySnapshot> BuildSnapshots(IBattleChara player)
    {
        var target = services.TargetManager.Target as IBattleChara;


        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var nextAction, out _))
        {
            var shapeKind = nextAction.Shape switch
            {
                RsrAoeShape.Cone         => DecisionOverlayShapeKind.Cone,
                RsrAoeShape.StraightLine => DecisionOverlayShapeKind.Rectangle,
                _                        => DecisionOverlayShapeKind.Circle
            };
            var actionOrigin = this.ResolveNextActionOrigin(player, nextAction);
            var rotation = MathF.Atan2(
                nextAction.PrimaryTargetPosition.X - actionOrigin.X,
                nextAction.PrimaryTargetPosition.Z - actionOrigin.Z);
            yield return new(
                DecisionOverlaySource.NextAction,
                DecisionOverlayState.Future,
                nextAction.ActionName,
                null,
                5,
                [new(shapeKind, DecisionOverlayState.Future, actionOrigin, nextAction.EffectRange, nextAction.HalfWidth, nextAction.EffectRange, rotation)],
                [],
                []);
        }

        var healerAoe = healerAoePositioningController.Overlay;
        if (healerAoe != null)
        {
            yield return new(
                DecisionOverlaySource.HealerCoverage,
                DecisionOverlayState.Candidate,
                $"Heal: {healerAoe.CurrentHits} -> {healerAoe.BestHits}",
                healerAoe.ActionName,
                35,
                [new(DecisionOverlayShapeKind.Circle, DecisionOverlayState.Candidate, healerAoe.Candidate, healerAoe.Radius, 0f, 0f, 0f)],
                [new(DecisionOverlayState.Candidate, player.Position, healerAoe.Candidate, "Heal")],
                healerAoe.Targets.Select(t => new DecisionOverlayMarker(
                    t.Hit ? DecisionOverlayState.Active : DecisionOverlayState.Suppressed,
                    t.Position, t.Radius, t.Hit ? null : null)).ToArray());
        }

        var aoe = aoePackPositioningController.Overlay;
        if (aoe != null)
        {
            if (string.Equals(aoe.ActionName, "Pack engagement", StringComparison.Ordinal))
            {
                // Approach ring — PrimaryTarget is the pack centroid, Radius is the engagement range
                var centroid = aoe.PrimaryTarget;
                var rangeLabel = $"{aoe.Radius:0}y";
                yield return new(
                    DecisionOverlaySource.AoE,
                    DecisionOverlayState.Candidate,
                    "Engage",
                    rangeLabel,
                    40,
                    [new(DecisionOverlayShapeKind.Circle, DecisionOverlayState.Candidate, centroid, aoe.Radius, 0f, aoe.Radius, 0f)],
                    [new(DecisionOverlayState.Candidate, player.Position, centroid, rangeLabel)],
                    aoe.Targets.Select(t => new DecisionOverlayMarker(DecisionOverlayState.Suppressed, t.Position, t.Radius, null)).ToArray());
            }
            else
            {
                var rotation = MathF.Atan2(aoe.PrimaryTarget.X - aoe.Candidate.X, aoe.PrimaryTarget.Z - aoe.Candidate.Z);
                var shapeKind = aoe.Shape switch
                {
                    nameof(RsrAoeShape.Cone)         => DecisionOverlayShapeKind.Cone,
                    nameof(RsrAoeShape.StraightLine) => DecisionOverlayShapeKind.Rectangle,
                    _                                => DecisionOverlayShapeKind.Circle
                };
                var label = aoePackPositioningController.RsrHenchedActive
                    ? $"AoE: {aoe.CurrentHits}→{aoe.BestHits} +RSR"
                    : $"AoE: {aoe.CurrentHits}→{aoe.BestHits}";
                yield return new(
                    DecisionOverlaySource.AoE,
                    DecisionOverlayState.Candidate,
                    label,
                    aoe.ActionName,
                    40,
                    [new(shapeKind, DecisionOverlayState.Candidate, aoe.Candidate, aoe.Radius, aoe.HalfWidth, aoe.Radius, rotation)],
                    [new(DecisionOverlayState.Candidate, player.Position, aoe.Candidate, "AoE")],
                    aoe.Targets.Select(t => new DecisionOverlayMarker(
                        t.InsideAvoidedHitbox ? DecisionOverlayState.Rejected
                            : t.Hit ? DecisionOverlayState.Active : DecisionOverlayState.Rejected,
                        t.Position, t.Radius,
                        t.InsideAvoidedHitbox ? t.AvoidanceLabel ?? "inside" : t.Hit ? null : "miss")).ToArray());
            }
        }

        // AoE suggestion — better position found but still evaluating (debounce not yet elapsed)
        var suggestion = aoePackPositioningController.SuggestedCandidate;
        if (suggestion != null && aoe == null)
        {
            var rotation = MathF.Atan2(suggestion.PrimaryTarget.X - suggestion.Candidate.X, suggestion.PrimaryTarget.Z - suggestion.Candidate.Z);
            var shapeKind = suggestion.Shape switch
            {
                nameof(RsrAoeShape.Cone)         => DecisionOverlayShapeKind.Cone,
                nameof(RsrAoeShape.StraightLine) => DecisionOverlayShapeKind.Rectangle,
                _                                => DecisionOverlayShapeKind.Circle
            };
            yield return new(
                DecisionOverlaySource.AoE,
                DecisionOverlayState.Future,
                $"AoE: {suggestion.CurrentHits}→{suggestion.BestHits}?",
                suggestion.ActionName,
                39,
                [new(shapeKind, DecisionOverlayState.Future, suggestion.Candidate, suggestion.Radius, suggestion.HalfWidth, suggestion.Radius, rotation)],
                [new(DecisionOverlayState.Future, player.Position, suggestion.Candidate, "AoE?")],
                suggestion.Targets.Select(t => new DecisionOverlayMarker(
                    t.Hit ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed,
                    t.Position, t.Radius, null)).ToArray());
        }


        passageOfArmsPositioningController.RefreshOverlay();
        var passage = passageOfArmsPositioningController.Overlay;
        if (passage != null)
        {
            var state = passage.Injected
                ? passage.PlayerInCone ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            yield return new(
                DecisionOverlaySource.PassageOfArms,
                state,
                passage.PlayerInCone ? "Passage: inside" : "Passage",
                passage.PaladinName,
                45,
                [new(DecisionOverlayShapeKind.Cone, state, passage.PaladinPosition, passage.Radius, passage.HalfAngle, passage.Radius, passage.RotationRadians)],
                [new(state, player.Position, passage.PreferredPosition, "Passage")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, passage.PaladinPosition, 0.35f, "PLD"),
                    new DecisionOverlayMarker(state, passage.PreferredPosition, 0.35f, "Wings")
                ]);
        }

        partyGravityPositioningController.RefreshOverlay();
        var partyGravity = partyGravityPositioningController.Overlay;
        if (partyGravity != null)
        {
            var state = partyGravity.Injected
                ? partyGravity.DistanceToCluster <= partyGravity.PullRadius ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            yield return new(
                DecisionOverlaySource.PartyGravity,
                state,
                partyGravity.DistanceToCluster <= partyGravity.PullRadius ? "Party: grouped" : "Party",
                $"{partyGravity.Members.Count} members",
                46,
                [
                    new(DecisionOverlayShapeKind.Circle, state, partyGravity.Center, partyGravity.PullRadius, 0f, 0f, 0f)
                ],
                partyGravity.DistanceToCluster > partyGravity.PullRadius
                    ? [new DecisionOverlayLine(state, player.Position, partyGravity.Center, "Party")]
                    : [],
                partyGravity.Members
                    .Select(member => new DecisionOverlayMarker(DecisionOverlayState.Candidate, member, 0.35f, null))
                    .Append(new DecisionOverlayMarker(DecisionOverlayState.Rejected, partyGravity.Center, 0.25f, "Center"))
                    .ToArray());
        }

        survivabilityZonePositioningController.RefreshOverlay();
        var survZone = survivabilityZonePositioningController.Overlay;
        if (survZone != null)
        {
            var state = survZone.Injected
                ? survZone.PlayerInZone ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            var label = survZone.PlayerInZone ? $"{survZone.ZoneName}: inside" : survZone.ZoneName;
            yield return new(
                DecisionOverlaySource.SurvivabilityZone,
                state,
                label,
                survZone.CasterName,
                44,
                [new(DecisionOverlayShapeKind.Circle, state, survZone.ZoneCenter, survZone.Radius, 0f, 0f, 0f)],
                survZone.PlayerInZone
                    ? []
                    : [new DecisionOverlayLine(state, player.Position, survZone.ZoneCenter, survZone.ZoneName)],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, survZone.CasterPosition, 0.35f, survZone.ZoneName[..3]),
                    new DecisionOverlayMarker(state, survZone.ZoneCenter, 0.25f, "Zone")
                ]);
        }

        this.AddGapCloserSnapshot(player, target, out var gapCloserSnapshot);
        if (gapCloserSnapshot != null)
        {
            yield return gapCloserSnapshot;
        }

        if (config.UseEscapeGapCloser)
        {
            var escapeDest = escapeGapCloserController.LastSafeEscapeDestination;
            if (escapeDest.HasValue)
            {
                yield return new(
                    DecisionOverlaySource.EscapeLanding,
                    DecisionOverlayState.Future,
                    "Escape",
                    null,
                    55,
                    [],
                    [new(DecisionOverlayState.Future, player.Position, escapeDest.Value, "Escape")],
                    [new(DecisionOverlayState.Future, escapeDest.Value, 0.35f, null)]);
            }
        }

        if (bossModSafety.TryGetSafeMovementIntent(player.Position, out var destination, out _))
        {
            yield return new(
                DecisionOverlaySource.FinalMovement,
                DecisionOverlayState.Active,
                "Move: BMR",
                null,
                100,
                [],
                [new(DecisionOverlayState.Active, player.Position, destination, "Move")],
                [new(DecisionOverlayState.Active, destination, 0.35f, null)]);
        }
    }

    private Vector3 ResolveNextActionOrigin(IBattleChara player, RsrAoeActionSnapshot nextAction)
    {
        if (nextAction.IsFriendly && nextAction.Shape == RsrAoeShape.Circle)
        {
            return healerAoePositioningController.Overlay?.Candidate ?? player.Position;
        }

        var injectedOverlay = aoePackPositioningController.Overlay;
        var suggestedOverlay = aoePackPositioningController.SuggestedCandidate;
        if (injectedOverlay != null &&
            injectedOverlay.ActionId == nextAction.AdjustedActionId)
        {
            return injectedOverlay.Candidate;
        }

        if (suggestedOverlay != null &&
            suggestedOverlay.ActionId == nextAction.AdjustedActionId)
        {
            return suggestedOverlay.Candidate;
        }

        var targetCenteredCircle = nextAction.Shape == RsrAoeShape.Circle &&
                                   nextAction.Range > nextAction.EffectRange + 3f;
        return targetCenteredCircle ? nextAction.PrimaryTargetPosition : player.Position;
    }


    private void AddGapCloserSnapshot(IBattleChara player, IBattleChara? target, out DecisionOverlaySnapshot? snapshot)
    {
        snapshot = null;
        if (!config.UseGapCloser && !config.UseEscapeGapCloser)
        {
            return;
        }

        var reason = config.UseEscapeGapCloser ? escapeGapCloserController.LastEscapeGapCloserSafety : gapCloserController.LastGapCloserSafety;
        var rawState = reason.Contains("used ", StringComparison.OrdinalIgnoreCase)
            ? DecisionOverlayState.Active
            : reason.Contains("current position safe", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("not in gap closer range", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("animation lock", StringComparison.OrdinalIgnoreCase)
                ? DecisionOverlayState.Suppressed
                : reason.Contains("safe", StringComparison.OrdinalIgnoreCase)
                    ? DecisionOverlayState.Candidate
                    : DecisionOverlayState.Rejected;

        var now = DateTime.UtcNow;
        if (rawState != this.gapCloserPendingState)
        {
            this.gapCloserPendingState = rawState;
            this.gapCloserPendingStateAt = now;
        }
        else if (now - this.gapCloserPendingStateAt >= GapCloserStateDebounce)
        {
            this.gapCloserDisplayedState = rawState;
        }

        var state = this.gapCloserDisplayedState;
        if (target == null)
        {
            return;
        }

        var landingPos = gapCloserController.LastSafeLandingPosition;
        var landingMarker = state == DecisionOverlayState.Candidate && landingPos.HasValue
            ? new DecisionOverlayMarker(DecisionOverlayState.Future, landingPos.Value, 0.35f, "Land")
            : null;

        snapshot = new(
            DecisionOverlaySource.GapCloser,
            state,
            state == DecisionOverlayState.Rejected ? "Gap: unsafe" : "Gap",
            reason,
            50,
            [],
            [new(state, player.Position, target.Position, "Gap")],
            landingMarker != null
                ? [new(state, target.Position, target.HitboxRadius, null), landingMarker]
                : [new(state, target.Position, target.HitboxRadius, null)]);

    }


    private IEnumerable<IBattleChara> GetVisiblePartyMembers(IBattleChara player)
    {
        return PartyAllyProvider.EnumerateVisiblePartyAllies(services, player);
    }

    private void DrawSnapshot(ImDrawListPtr drawList, DecisionOverlaySnapshot snapshot, int labelStackIndex = 0)
    {
        var color = ColorFor(snapshot.State, snapshot.Source);
        foreach (var shape in snapshot.Shapes)
        {
            this.DrawShape(drawList, shape, ColorFor(shape.State, snapshot.Source));
        }

        foreach (var line in snapshot.Lines)
        {
            this.DrawLine(drawList, line.From, line.To, ColorFor(line.State, snapshot.Source), thickness: snapshot.Source == DecisionOverlaySource.FinalMovement ? 3f : 2f);
        }

        foreach (var marker in snapshot.Markers)
        {
            this.DrawCircle(drawList, marker.Position, Math.Max(marker.Radius, 0.25f), ColorFor(marker.State, snapshot.Source), thickness: 2f);
            if (marker.Label != null)
            {
                this.DrawLabel(drawList, marker.Position, marker.Label, ColorFor(marker.State, snapshot.Source));
            }
        }

        var labelAnchor = snapshot.Markers.FirstOrDefault()?.Position ??
                          snapshot.Shapes.FirstOrDefault()?.Origin ??
                          snapshot.Lines.FirstOrDefault()?.To;
        if (labelAnchor.HasValue)
        {
            this.DrawLabel(drawList, labelAnchor.Value, snapshot.Label, color, pixelYOffset: labelStackIndex * 16f);
        }
    }

    private void DrawShape(ImDrawListPtr drawList, DecisionOverlayShape shape, uint color)
    {
        switch (shape.Kind)
        {
            case DecisionOverlayShapeKind.Circle:
                this.DrawCircle(drawList, shape.Origin, shape.Radius, color, 2f);
                break;
            case DecisionOverlayShapeKind.Cone:
                this.DrawCone(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, color);
                break;
            case DecisionOverlayShapeKind.Rectangle:
                this.DrawRectangle(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, color);
                break;
        }
    }

    private void DrawCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color, float thickness)
    {
        const int Segments = 64;
        Vector2? previous = null;
        for (var i = 0; i <= Segments; ++i)
        {
            var angle = i * MathF.Tau / Segments;
            var point = center + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            if (!this.Project(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous.HasValue)
            {
                drawList.AddLine(previous.Value, screen, color, thickness);
            }

            previous = screen;
        }
    }

    private void DrawCone(ImDrawListPtr drawList, Vector3 center, float radius, float rotation, float halfWidth, uint color)
    {
        var left = center + Direction(rotation - halfWidth) * radius;
        var right = center + Direction(rotation + halfWidth) * radius;
        this.DrawLine(drawList, center, left, color, 2f);
        this.DrawLine(drawList, center, right, color, 2f);

        const int Segments = 24;
        Vector2? previous = null;
        for (var i = 0; i <= Segments; ++i)
        {
            var angle = rotation - halfWidth + 2f * halfWidth * i / Segments;
            var point = center + Direction(angle) * radius;
            if (!this.Project(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous.HasValue)
            {
                drawList.AddLine(previous.Value, screen, color, 2f);
            }

            previous = screen;
        }
    }

    private void DrawRectangle(ImDrawListPtr drawList, Vector3 origin, float rotation, float length, float halfWidth, uint color)
    {
        var forward = Direction(rotation);
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var p1 = origin + side * halfWidth;
        var p2 = origin - side * halfWidth;
        var p3 = origin + forward * length - side * halfWidth;
        var p4 = origin + forward * length + side * halfWidth;
        this.DrawLine(drawList, p1, p2, color, 2f);
        this.DrawLine(drawList, p2, p3, color, 2f);
        this.DrawLine(drawList, p3, p4, color, 2f);
        this.DrawLine(drawList, p4, p1, color, 2f);
    }

    private void DrawLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness)
    {
        if (this.Project(from, out var fromScreen) && this.Project(to, out var toScreen))
        {
            drawList.AddLine(fromScreen, toScreen, color, thickness);
        }
    }

    private void DrawLabel(ImDrawListPtr drawList, Vector3 world, string label, uint color, float pixelYOffset = 0f)
    {
        if (!this.Project(world, out var screen))
        {
            return;
        }

        var textSize = ImGui.CalcTextSize(label);
        // Center horizontally, stack upward from floor point; pixelYOffset shifts additional labels further up.
        var pos = screen + new Vector2(-textSize.X * 0.5f, -textSize.Y - 4f - pixelYOffset);
        drawList.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)), label);
        drawList.AddText(pos, color, label);
    }

    private bool Project(Vector3 world, out Vector2 screen)
    {
        return services.GameGui.WorldToScreen(world, out screen);
    }

    private static Vector3 Direction(float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return new Vector3(sin, 0f, cos);
    }

    private static uint ColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        if (source == DecisionOverlaySource.FinalMovement)
        {
            return ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        }

        return state switch
        {
            DecisionOverlayState.Active     => ImGui.GetColorU32(new Vector4(0.25f, 0.82f, 0.38f, 1f)),
            DecisionOverlayState.Candidate  => ImGui.GetColorU32(new Vector4(0.25f, 0.64f, 1f, 1f)),
            DecisionOverlayState.Future     => ImGui.GetColorU32(new Vector4(1f, 0.84f, 0.2f, 1f)),
            DecisionOverlayState.Rejected   => ImGui.GetColorU32(new Vector4(1f, 0.2f, 0.2f, 1f)),
            DecisionOverlayState.Suppressed => ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f)),
            _                               => ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f))
        };
    }
}
