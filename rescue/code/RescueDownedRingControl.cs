using Godot;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Test.code;

internal enum DownedRingDisplayMode
{
	Portrait,
	Corpse
}

internal partial class RescueDownedRingControl : Control
{
	private static readonly Color OutlineColor = new Color("000000");

	private static readonly Color SegmentBackgroundColor = new Color("202020");

	private static readonly Color FillColor = new Color("8A3FD2");

	// Visual tuning parameters for the downed ring.
	private const float SegmentGapDegrees = 14f;

	private const float WrapperRingPadding = 3.5f;

	private const float WrapperRingThickness = 2.6f;

	private const float SegmentOutlineExtraThickness = 2f;

	private const float SegmentDividerThickness = 2f;

	private const float RingThicknessScale = 0.33f;

	private const float RingThicknessMin = 4.5f;

	private const float RingThicknessMax = 10f;

	private const float PortraitRadiusPadding = 2f;

	private const float CorpseRadiusScale = 0.16f;

	private const float CorpseRadiusMin = 18f;

	private const float CorpseRadiusMax = 34f;

	private const float PortraitCenterOffsetX = 0f;

	private const float PortraitCenterOffsetY = 0f;

	private const float CorpseCenterOffsetX = 0f;

	private const float CorpseCenterOffsetY = 0f;

	private Player? _player;

	private DownedRingDisplayMode _displayMode;

	private bool _isActive;

	private int _downCount;

	private float _hpRatio;

	public override void _Ready()
	{
		TopLevel = false;
		SetProcess(true);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double _delta)
	{
		UpdateState();
	}

	public void Bind(Player player, DownedRingDisplayMode displayMode)
	{
		_player = player;
		_displayMode = displayMode;
		UpdateState(forceRedraw: true);
	}

	public override void _Draw()
	{
		if (!_isActive || _downCount <= 0)
		{
			return;
		}

		Vector2 center = GetRingCenter();
		float outerRadius = GetOuterRadius();
		if (outerRadius <= 3f)
		{
			return;
		}

		float ringThickness = Mathf.Clamp(outerRadius * RingThicknessScale, RingThicknessMin, RingThicknessMax);
		float ringRadius = outerRadius - ringThickness * 0.5f - 1f;
		float innerRadius = ringRadius - ringThickness * 0.5f;
		float outerSegmentRadius = ringRadius + ringThickness * 0.5f;
		float wrapperRadius = outerRadius + WrapperRingPadding;

		DrawArc(center, wrapperRadius, 0f, Mathf.Tau, 96, OutlineColor, WrapperRingThickness, true);
		DrawArc(center, outerRadius, 0f, Mathf.Tau, 96, OutlineColor, 2.4f, true);

		float activeSegmentProgress = Mathf.Clamp(_downCount, 0, 3) * Mathf.Clamp(_hpRatio, 0f, 1f);

		for (int segmentIndex = 0; segmentIndex < 3; segmentIndex++)
		{
			(float startAngle, float endAngle) = GetSegmentAngles(segmentIndex);
			float segmentSweep = endAngle - startAngle;

			DrawArc(center, ringRadius, startAngle, endAngle, 24, OutlineColor, ringThickness + SegmentOutlineExtraThickness, true);
			DrawArc(center, ringRadius, startAngle, endAngle, 24, SegmentBackgroundColor, ringThickness, true);

			float segmentFill = Mathf.Clamp(activeSegmentProgress - segmentIndex, 0f, 1f);
			if (segmentFill > 0f)
			{
				float fillEnd = startAngle + segmentSweep * segmentFill;
				DrawArc(center, ringRadius, startAngle, fillEnd, 20, FillColor, ringThickness, true);
			}

			Vector2 startDirection = Vector2.Right.Rotated(startAngle);
			Vector2 endDirection = Vector2.Right.Rotated(endAngle);
			DrawLine(center + startDirection * innerRadius, center + startDirection * outerSegmentRadius, OutlineColor, SegmentDividerThickness, true);
			DrawLine(center + endDirection * innerRadius, center + endDirection * outerSegmentRadius, OutlineColor, SegmentDividerThickness, true);
		}
	}

	private void UpdateState(bool forceRedraw = false)
	{
		if (_player == null)
		{
			if (Visible)
			{
				Visible = false;
			}
			return;
		}

		bool hasData = RescueCorpseManager.TryGetDownedRingData(_player, out int downCount, out float hpRatio, out bool isActive);
		bool shouldShow = hasData && isActive && downCount > 0;
		downCount = Mathf.Clamp(downCount, 0, 3);
		hpRatio = Mathf.Clamp(hpRatio, 0f, 1f);

		bool changed = forceRedraw || shouldShow != _isActive || downCount != _downCount || !Mathf.IsEqualApprox(hpRatio, _hpRatio);
		_isActive = shouldShow;
		_downCount = downCount;
		_hpRatio = hpRatio;
		Visible = shouldShow;
		if (changed)
		{
			QueueRedraw();
		}
	}

	private Vector2 GetRingCenter()
	{
		if (_displayMode == DownedRingDisplayMode.Portrait)
		{
			return Size * 0.5f + new Vector2(PortraitCenterOffsetX, PortraitCenterOffsetY);
		}
		return Size * 0.5f + new Vector2(CorpseCenterOffsetX, CorpseCenterOffsetY);
	}

	private float GetOuterRadius()
	{
		float minSize = Mathf.Min(Size.X, Size.Y);
		float safetyPadding = WrapperRingPadding + WrapperRingThickness;
		if (_displayMode == DownedRingDisplayMode.Portrait)
		{
			return Mathf.Max(0f, minSize * 0.5f - (PortraitRadiusPadding + safetyPadding));
		}
		return Mathf.Clamp(minSize * CorpseRadiusScale, CorpseRadiusMin, CorpseRadiusMax) - safetyPadding;
	}

	private static (float start, float end) GetSegmentAngles(int segmentIndex)
	{
		float segmentStartDeg = -60f + 120f * segmentIndex;
		float startDeg = segmentStartDeg + SegmentGapDegrees * 0.5f;
		float endDeg = segmentStartDeg + 120f - SegmentGapDegrees * 0.5f;
		return (Mathf.DegToRad(startDeg), Mathf.DegToRad(endDeg));
	}
}
