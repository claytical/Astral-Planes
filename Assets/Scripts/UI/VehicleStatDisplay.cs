using TMPro;
using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Display")]
public class VehicleStatDisplay : MaskableGraphic
{
    [Header("Dimensions")]
    public float axisLength = 60f;
    [Range(0.5f, 4f)] public float axisLineWidth = 1.5f;

    [Header("Colors")]
    public Color bgDiamondColor = new Color(0.15f, 0.15f, 0.2f, 0.2f);
    public Color axisColor = new Color(1f, 1f, 1f, 0.2f);
    public Color statFillColor = new Color(0.4f, 0.8f, 1f, 0.4f);
    public Color statEdgeColor = new Color(0.5f, 0.85f, 1f, 0.9f);

    [Header("Labels")]
    public TMP_Text speedLabel;
    public TMP_Text agilityLabel;
    public TMP_Text powerLabel;
    public TMP_Text fuelLabel;

    private float _speed = 0.5f;
    private float _agility = 0.5f;
    private float _power = 0.5f;
    private float _fuel = 0.5f;

    // Speed=up, Agility=right, Power=down, Fuel=left
    private static readonly Vector2[] Axes = {
        Vector2.up, Vector2.right, Vector2.down, Vector2.left
    };

    public void SetStats(float speed, float agility, float power, float fuel)
    {
        _speed = speed;
        _agility = agility;
        _power = power;
        _fuel = fuel;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        float[] vals = { _speed, _agility, _power, _fuel };
        var statPts = new Vector2[4];
        for (int i = 0; i < 4; i++)
            statPts[i] = Axes[i] * (vals[i] * axisLength);

        AddPolygon(vh, Axes, axisLength, bgDiamondColor);
        for (int i = 0; i < 4; i++)
            AddLine(vh, Vector2.zero, Axes[i] * axisLength, axisLineWidth, axisColor);
        AddPolygon(vh, statPts, 1f, statFillColor);
        for (int i = 0; i < 4; i++)
            AddLine(vh, statPts[i], statPts[(i + 1) % 4], axisLineWidth, statEdgeColor);
    }

    private static void AddPolygon(VertexHelper vh, Vector2[] pts, float scale, Color col)
    {
        int b = vh.currentVertCount;
        UIVertex v = new UIVertex();
        v.color = col;
        v.position = Vector3.zero;
        vh.AddVert(v);
        for (int i = 0; i < pts.Length; i++)
        {
            v.position = new Vector3(pts[i].x * scale, pts[i].y * scale, 0f);
            vh.AddVert(v);
        }
        for (int i = 0; i < pts.Length; i++)
            vh.AddTriangle(b, b + i + 1, b + (i + 1) % pts.Length + 1);
    }

    private static void AddLine(VertexHelper vh, Vector2 from, Vector2 to, float width, Color col)
    {
        Vector2 d = to - from;
        if (d.sqrMagnitude < 0.0001f) return;
        Vector2 p = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
        int b = vh.currentVertCount;
        UIVertex v = new UIVertex();
        v.color = col;
        v.position = new Vector3(from.x - p.x, from.y - p.y, 0f); vh.AddVert(v);
        v.position = new Vector3(from.x + p.x, from.y + p.y, 0f); vh.AddVert(v);
        v.position = new Vector3(to.x + p.x, to.y + p.y, 0f);     vh.AddVert(v);
        v.position = new Vector3(to.x - p.x, to.y - p.y, 0f);     vh.AddVert(v);
        vh.AddTriangle(b, b + 1, b + 2);
        vh.AddTriangle(b, b + 2, b + 3);
    }
}
