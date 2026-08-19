using UnityEngine;

public partial class CosmicDust
{
    [Header("Flower Bloom (vehicle-carve regrow tell)")]
    [SerializeField] private GameObject flowerRoot;
    [SerializeField] private SpriteRenderer flowerDiamondA;
    [SerializeField] private SpriteRenderer flowerDiamondB;
    [SerializeField] private float flowerBloomAlpha = 0.404f;
    [SerializeField] private float flowerRotationSpeedDegPerSec = 12f;

    public bool HasBloomedFlower { get; private set; }

    public void BloomFlower(Color roleColorRgb)
    {
        HasBloomedFlower = true;
        if (flowerRoot != null) flowerRoot.SetActive(true);
        var c = roleColorRgb;
        c.a = flowerBloomAlpha;
        if (flowerDiamondA != null) flowerDiamondA.color = c;
        if (flowerDiamondB != null) flowerDiamondB.color = c;
    }

    public void HideFlower()
    {
        HasBloomedFlower = false;
        if (flowerRoot != null) flowerRoot.SetActive(false);
    }

    // Counter-rotating petals read as the flower slowly opening while bloomed. Each petal
    // keeps whatever angle it was at when last hidden, so re-blooming resumes rather than snaps.
    private void TickFlowerRotation()
    {
        if (!HasBloomedFlower) return;
        float delta = flowerRotationSpeedDegPerSec * Time.deltaTime;
        if (flowerDiamondA != null) flowerDiamondA.transform.Rotate(0f, 0f, delta);
        if (flowerDiamondB != null) flowerDiamondB.transform.Rotate(0f, 0f, -delta);
    }
}
