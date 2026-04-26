using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

// Tiny pointer-event source so the PushToTalk sample scene can wire
// PressTalk()/ReleaseTalk() directly without depending on EventTrigger's
// built-in script GUID (which differs across Unity installs).
public class HoldToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public UnityEvent onPointerDown = new UnityEvent();
    public UnityEvent onPointerUp = new UnityEvent();

    public void OnPointerDown(PointerEventData eventData) => onPointerDown?.Invoke();
    public void OnPointerUp(PointerEventData eventData) => onPointerUp?.Invoke();
}
