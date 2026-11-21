using System;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const int W=1240,H=760;
        static readonly Color Ink=new Color32(246,244,235,255),Card=new Color32(255,254,249,255),
            Field=new Color32(223,233,225,255),Light=new Color32(23,45,42,255),Muted=new Color32(68,90,82,255),
            Mint=new Color32(0,100,80,255),Gold=new Color32(132,76,15,255),Deep=new Color32(20,57,49,255),
            Stage=new Color32(20,40,38,255),Divider=new Color32(195,208,194,255),
            HeaderText=new Color32(247,248,235,255),Lime=new Color32(202,232,145,255);


        RectTransform Rect(string name,Transform parent,float x,float y,float w,float h)
        {
            var t=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;t.SetParent(parent,false);
            t.anchorMin=t.anchorMax=t.pivot=new Vector2(0,1);t.anchoredPosition=new Vector2(x,-y);t.sizeDelta=new Vector2(w,h);
            return t;
        }
        Image Box(string name,Transform parent,float x,float y,float w,float h,Color color)
        {
            var image=Rect(name,parent,x,y,w,h).gameObject.AddComponent<Image>();image.color=color;image.raycastTarget=false;return image;
        }
        Text Label(string name,Transform parent,float x,float y,float w,float h,string value,int size,Color color,TextAnchor anchor=TextAnchor.MiddleLeft)
        {
            var t=Rect(name,parent,x,y,w,h).gameObject.AddComponent<Text>();
            t.font=koreanFont!=null?koreanFont:Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.raycastTarget=false;t.supportRichText=false;t.color=color;t.text=value??"";t.fontSize=size;t.alignment=anchor;
            t.verticalOverflow=VerticalWrapMode.Truncate;t.horizontalOverflow=HorizontalWrapMode.Wrap;
            t.resizeTextForBestFit=true;t.resizeTextMinSize=Mathf.Min(20,size);t.resizeTextMaxSize=size;return t;
        }
        Text BoldLabel(string name,Transform parent,float x,float y,float w,float h,string value,int size,Color color,TextAnchor anchor=TextAnchor.MiddleLeft)
        {
            var text=Label(name,parent,x,y,w,h,value,size,color,anchor);
            text.fontStyle=FontStyle.Bold;return text;
        }
        Image RoundBox(string name,Transform parent,float x,float y,float w,float h,Color color,float radius=12)
        {
            var im=Box(name,parent,x,y,w,h,color);
            im.sprite=MeetingSurface.Rounded;im.type=Image.Type.Sliced;
            im.pixelsPerUnitMultiplier=16f/Mathf.Clamp(radius,1,Mathf.Min(w,h)/2);
            return im;
        }
        RelayHit Button(string id,float x,float y,float w,float h,string label,Action action,bool primary=false,Transform parent=null,string role=null)
        {
            var im=RoundBox(id,parent??content,x,y,w,h,primary?Mint:Field,10);
            var collider=im.gameObject.AddComponent<BoxCollider>();collider.size=new Vector3(w,h,3);collider.center=new Vector3(w/2,-h/2,-1.5f);
            var hit=im.gameObject.AddComponent<RelayHit>();hit.background=im;hit.resting=im.color;hit.clicked=action;hit.role=role;
            hit.focus=RoundBox("Focus",im.transform,4,7,4,h-14,primary?Lime:Mint,2).gameObject;hit.focus.SetActive(false);
            BoldLabel("Caption",im.transform,9,2,w-18,h-4,label,23,primary?Card:Light,TextAnchor.MiddleCenter);
            return hit;
        }
        void ClearLayer(ref Transform layer)
        {
            if(layer==null)return;
            layer.gameObject.SetActive(false);Destroy(layer.gameObject);layer=null;
        }
        void Draw()
        {
            if (panel == null) return;
            hovered = null;
            ClearLayer(ref content);
            if (SpatialAssembly)
            {
                DrawSpatialMeeting();
            }
            else
            {
                content = Rect("Meeting", panel, 0, 0, W, H);
                RoundBox("Background", content, 0, 0, W, H, Ink, 18);
                RoundBox("Masthead", content, 0, 0, W, 70, Deep, 18);
                BoldLabel("Brand", content, 36, 13, 510, 45, "ContextPin XR", 28, HeaderText);
                DrawMeeting();
                Label("Notice", content, 36, 696, 1150, 52, notice, 20, Gold);
            }
            Physics.SyncTransforms();
        }
        void Heading(string title,string description)
        {
            BoldLabel("Title",content,36,86,1168,60,title,43,Light);
            Label("Description",content,38,147,1164,43,description,23,Muted);
        }
    }
}
