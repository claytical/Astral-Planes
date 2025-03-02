using System.Collections;
using UnityEngine;

public enum ObstacleType
{
    Standard,
    Void,
    Hazard
}
public class ObstacleMovement : MonoBehaviour
{
    // The current grid cell (using integer coordinates)
    public Vector2Int gridPosition;
    // Duration for each easing move.
    public float movementDuration = 8f;
    // Time between moves (should match your drum beat)
    public float beatInterval = 1.6f;
    public int candidateStep;
    // Flag to prevent overlapping moves.
    private bool isMoving = false;
    private DrumTrack drumTrack;
    // Custom grid parameters
    public Vector2 gridOrigin;  // e.g. (screenMinX + xOffset, obstacleInitialY)
    public Vector2 cellSize;    // e.g. (stepWidth, desiredYStep)

    public void Init(Vector2 startPosition)
    {
        // Initialize grid parameters if they haven't been set already.

        transform.position = startPosition;
        beatInterval = 1.6f;
        // Start the movement loop.
        StartCoroutine(MoveLoop());
    }

    public void SetDrumTrack(DrumTrack drums)
    {
        drumTrack = drums;
    }
    void ApplyCollisionForce()
    {
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.linearVelocity = new Vector2(0.01f, 0.01f); // ✅ Apply tiny force to register collisions
        }
    }

    IEnumerator MoveLoop()
    {

        while (true)
        {
            yield return new WaitForSeconds(beatInterval);

            if (drumTrack.obstacleMoveDelay > 0f) // ✅ If delay is greater than 0, move obstacles randomly over time
            {
                if (drumTrack.activeObstacles.Count > 0)
                {
                    yield return new WaitForSeconds(drumTrack.obstacleMoveDelay); // ✅ Wait before moving next obstacle
                
                    int randomIndex = Random.Range(0, drumTrack.activeObstacles.Count);
                    GameObject selectedObstacle = drumTrack.activeObstacles[randomIndex];

                    if (selectedObstacle != null)
                    {
                        ObstacleMovement movement = selectedObstacle.GetComponent<ObstacleMovement>();
                        if (movement != null && !movement.isMoving) 
                        {
                            movement.TryMove();
                        }
                    }
                }
            }
            else // ✅ If delay is 0, move all obstacles at the same time
            {
                TryMove();
            }
        }
    }

    public void TryMove()
    {
        if (!gameObject.activeInHierarchy) // ✅ Ensure object is active
        {
            Debug.LogWarning($"{gameObject.name} is inactive. Cannot start MoveLoop coroutine.");
            return;
        }

        if (isMoving) return;

        Vector3 targetPos = transform.position + new Vector3(0, 1.0f, 0); // Move up by 1 unit
        StartCoroutine(MoveToPosition(targetPos));
    }

    IEnumerator MoveToPosition(Vector3 targetPos)
    {
        isMoving = true;
        Vector3 startPos = transform.position;
        float elapsed = 0f;
        float moveDuration = 0.5f; // Speed of movement (adjust if needed)

        while (elapsed < moveDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / moveDuration);
            transform.position = Vector3.Lerp(startPos, targetPos, t);
            yield return null;
        }

        transform.position = targetPos;
        isMoving = false;
    }

    void Update()
    {
        float screenMaxY = Camera.main.ViewportToWorldPoint(new Vector3(0.5f, 1, 0)).y;
        if (transform.position.y > screenMaxY + 1f) // Small buffer
        {
            if (gameObject != null)
            {
                if (drumTrack != null)
                {
                    drumTrack.activeObstacles.Remove(gameObject); // Remove from tracking list
                }
                Destroy(gameObject);
            }
        }
    }

}
