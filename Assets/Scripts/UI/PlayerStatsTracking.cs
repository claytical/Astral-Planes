using UnityEngine;
/// <summary>
/// NOT INTEGRATED
/// </summary>
public class PlayerStatsTracking : MonoBehaviour
{
    public int score = 0;
    public float distanceCovered = 0f;
    public int obstaclesAvoided = 0;
    public int itemsCollected = 0;
    public int damageTaken = 0;
    public int assists = 0;
    public float fuelUsed = 0;
    public float timeTaken;
    public int maxEnergy;  // Example value
    public float maxTime;
    public float effiencyScore;

    // Competitive-mode scoring (driven by CompetitiveScoring; separate from the legacy `score` above).
    public int CompetitiveTotal { get; private set; }      // points, multiplier applied
    public int RoundNoteScore { get; private set; }        // raw notes placed this round, multiplier NOT applied
    public bool RoundActive => _roundBursts > 0;           // collected from a discovery burst that hasn't cleared
    public int ActiveMultiplier => _multiplierBursts > 0 ? _multiplierValue : 0;   // 0 = none held
    public event System.Action CompetitiveChanged;

    private int _roundBursts;
    private int _multiplierBursts;
    private int _multiplierValue;

    /// <summary>First note collected from a discovery burst. multiplier &gt; 0 only for the burst's multiplier holder.</summary>
    public void JoinRound(int multiplier)
    {
        if (_roundBursts == 0) RoundNoteScore = 0;
        _roundBursts++;
        if (multiplier > 0)
        {
            _multiplierBursts++;
            _multiplierValue = multiplier;
        }
        CompetitiveChanged?.Invoke();
    }

    public void LeaveRound(bool heldMultiplier)
    {
        if (_roundBursts <= 0) return;
        _roundBursts--;
        if (heldMultiplier && _multiplierBursts > 0) _multiplierBursts--;
        CompetitiveChanged?.Invoke();
    }

    public void AddNotePoints(int totalPoints, int roundPoints)
    {
        CompetitiveTotal += totalPoints;
        RoundNoteScore += roundPoints;
        CompetitiveChanged?.Invoke();
    }

    private Vector3 lastPosition;

    void Start()
    {
        // Record the initial position to start tracking distance
        lastPosition = transform.position;
    }

    void Update()
    {
        // Update the distance covered by the player
        TrackDistance();
    }

    private void TrackDistance()
    {
        // Calculate the distance moved since the last frame
        float distanceThisFrame = Vector3.Distance(transform.position, lastPosition);
        distanceCovered += distanceThisFrame;

        // Update the last position to the current position
        lastPosition = transform.position;
    }

    public void AddScore(float points)
    {
        score += (int)points;
        // You might want to add a UI update here to reflect the new score
    }

    public void RecordObstacleAvoidance()
    {
        obstaclesAvoided++;
        AddScore(10);  // Example scoring, adjust as needed
    }

    public void RecordItemCollected()
    {
        itemsCollected++;
        AddScore(5);  // Example scoring, adjust as needed
    }

    public void RecordDamage(int damage)
    {
        damageTaken += damage;
        AddScore(-damage);  // Penalty for taking damage, adjust as needed
    }

    public void RecordAssist()
    {
        assists++;
        AddScore(15);  // Example scoring, adjust as needed
    }

    public void RecordFuelUsed(float amount)
    {
        fuelUsed += amount;
        AddScore(amount);  // Reward for managing fuel, adjust as needed
    }

    public float CalculateEfficiencyScore()
    {
        maxEnergy = 0;//GameFlowManager.Instance;
        if (timeTaken == 0) return 0;  // Avoid division by zero
        effiencyScore = (itemsCollected / (float)maxEnergy) * (maxTime / timeTaken);
        return effiencyScore;
    }

}
