using UnityEngine;

public interface IMotifCoralGeometryBuilder
{
    (int[] fullTris, int segCount) BuildTube(Mesh mesh, Vector3[] spine, float baseRadius, int tubeSides, float tapePower, float minTipRadius, float worldScale, Color col, float vertexAlpha);
}

public sealed class MotifCoralGeometryBuilder : IMotifCoralGeometryBuilder
{
    public (int[] fullTris, int segCount) BuildTube(Mesh mesh, Vector3[] spine, float baseRadius, int tubeSides, float tapePower, float minTipRadius, float worldScale, Color col, float vertexAlpha)
    {
        int rings = spine.Length; int segCount = rings - 1; int sides = Mathf.Max(3, tubeSides);
        int vCount = rings * sides; int idxCount = segCount * sides * 6;
        var verts = new Vector3[vCount]; var norms = new Vector3[vCount]; var uvs = new Vector2[vCount]; var cols = new Color[vCount]; var tris = new int[idxCount];
        Vector3 prevNorm = Vector3.up;
        Vector3 initTan = (spine[1]-spine[0]).normalized;
        if (Mathf.Abs(Vector3.Dot(initTan, prevNorm)) > 0.95f) prevNorm = Vector3.right;
        for(int r=0;r<rings;r++){
            Vector3 tangent = r<rings-1 ? (spine[r+1]-spine[r]) : (spine[r]-spine[r-1]); if(tangent.sqrMagnitude<1e-6f) tangent=prevNorm; tangent.Normalize();
            Vector3 normal = prevNorm - Vector3.Dot(prevNorm,tangent)*tangent; if(normal.sqrMagnitude<1e-6f){ normal=Vector3.Cross(tangent,Vector3.right); if(normal.sqrMagnitude<1e-6f) normal=Vector3.Cross(tangent,Vector3.up);} normal.Normalize();
            Vector3 binormal = Vector3.Cross(tangent,normal).normalized; prevNorm=normal; float t=rings<=1?0f:(float)r/(rings-1); float taper=Mathf.Pow(1f-t,tapePower); float radius=Mathf.Max(minTipRadius*worldScale,baseRadius*taper);
            for(int s=0;s<sides;s++){ float a=(float)s/sides*Mathf.PI*2f; Vector3 offset=(normal*Mathf.Cos(a)+binormal*Mathf.Sin(a))*radius; int vi=r*sides+s; verts[vi]=spine[r]+offset; norms[vi]=offset.normalized; uvs[vi]=new Vector2((float)s/sides,t); Color c=col; c.a=vertexAlpha*(1f-t*0.35f); cols[vi]=c; }
        }
        int ti=0; for(int r=0;r<segCount;r++) for(int s=0;s<sides;s++){ int sn=(s+1)%sides,a=r*sides+s,b=r*sides+sn,c=(r+1)*sides+s,d=(r+1)*sides+sn; tris[ti++]=a;tris[ti++]=c;tris[ti++]=b; tris[ti++]=b;tris[ti++]=c;tris[ti++]=d; }
        mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0,uvs); mesh.SetColors(cols); mesh.SetTriangles(tris,0,true); mesh.RecalculateBounds();
        return (tris,segCount);
    }
}
