using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Boundaries : MonoBehaviour
{
    public int damage = 0;
    private Collider2D[] edges;
    // Start is called before the first frame update
    void Start()
    {
        edges = GetComponentsInChildren<Collider2D>();
    }

    public void Ignore(Collider2D otherCollider)
    {
        for (int i = 0; i < edges.Length; i++)
        {
            if (edges[i] != otherCollider)
            {
                Debug.Log(("Ignoring Collider " + otherCollider.gameObject.name));
                Physics2D.IgnoreCollision(edges[i], otherCollider);
            }
        }
    }
}
