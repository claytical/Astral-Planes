using System;
using UnityEngine;
using Random = UnityEngine.Random;

public class MineNodeMovement : MonoBehaviour
{
    private Rigidbody2D rb2d;
    private int movementDirection;

    public float movementSpeed = 1;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        rb2d = GetComponent<Rigidbody2D>();
        SetDirection();
    }

    private void SetDirection()
    {
        movementDirection = Random.Range(0, 4);
    }
    // Update is called once per frame
    void FixedUpdate()
    {
        switch (movementDirection)
        {
            case 0: 
                rb2d.linearVelocity = Vector2.up * movementSpeed;
                break;
            case 1:
                rb2d.linearVelocity = Vector2.down * movementSpeed;
                break; 
            case 2:
                rb2d.linearVelocity = Vector2.left * movementSpeed;
                break; 
            case 3:
                rb2d.linearVelocity = Vector2.right * movementSpeed;
                break; 
            default:
                break;
        }
    }

    private void OnCollisionEnter2D(Collision2D other)
    {
        SetDirection();
    }
}
