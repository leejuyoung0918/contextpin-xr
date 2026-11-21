using UnityEngine;

namespace ContextPin
{
    internal static class TraceSurface
    {
        // A single nine-sliced alpha mask serves all rounded controls on the headset.
        static Sprite rounded;
        public static Sprite Rounded
        {
            get
            {
                if(rounded!=null)return rounded;
                const int side=40,corner=16;
                var pixels=new Color32[side*side];
                for(int y=0;y<side;y++)for(int x=0;x<side;x++)
                {
                    float dx=Mathf.Max(0,corner-Mathf.Min(x+.5f,side-x-.5f));
                    float dy=Mathf.Max(0,corner-Mathf.Min(y+.5f,side-y-.5f));
                    var alpha=(byte)Mathf.RoundToInt(255*Mathf.Clamp01(corner+.5f-Mathf.Sqrt(dx*dx+dy*dy)));
                    pixels[y*side+x]=new Color32(255,255,255,alpha);
                }
                var tex=new Texture2D(side,side,TextureFormat.RGBA32,false){name="Trace control mask",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear,hideFlags=HideFlags.HideAndDontSave};
                tex.SetPixels32(pixels);tex.Apply(false,true);
                rounded=Sprite.Create(tex,new Rect(0,0,side,side),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect,new Vector4(corner,corner,corner,corner));
                rounded.hideFlags=HideFlags.HideAndDontSave;
                return rounded;
            }
        }
    }
}
