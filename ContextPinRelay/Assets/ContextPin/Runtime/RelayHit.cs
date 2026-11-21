using System;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed class RelayHit : MonoBehaviour
    {
        public Action clicked;
        public Action<Vector3> pointClicked, dragged;
        public string meetingVersion;
        public Image background;
        public GameObject focus;
        public Color resting;
        public string role;
        public Vector3 Center => transform.TransformPoint(new Vector3(((RectTransform)transform).rect.width/2,-((RectTransform)transform).rect.height/2,0));
        public void Hover(bool value)
        {
            if(background!=null)background.color=value?Color.Lerp(resting,Color.black,.09f):resting;
            if(focus!=null)focus.SetActive(value);
        }
    }
}
