using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    // One row of a RemoteListView. Put this on your own row prefab and wire whichever fields your
    // design actually has — every reference is optional, so a row can be text-only.
    //
    // Bind and SetSelected are virtual: subclass this when a row needs app-specific visuals
    // (badges, progress, a second icon) instead of forking RemoteListView.
    [AddComponentMenu("Remote Control/Remote List Row")]
    public class RemoteListRow : MonoBehaviour
    {
        [Header("Content")]
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private TextMeshProUGUI _sublabel;
        [SerializeField] private Image _icon;

        [Header("Interaction")]
        [Tooltip("Empty => the Button on this object or in its children.")]
        [SerializeField] private Button _button;

        [Header("Selection Tint")]
        [Tooltip("Empty => the button's Target Graphic.")]
        [SerializeField] private Graphic _selectionGraphic;
        [SerializeField] private Color _selectedColor = Color.white;
        [SerializeField] private Color _unselectedColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        [Tooltip("Force Transition to None so the tint is not overwritten by the button's own hover/press colours.")]
        [SerializeField] private bool _overrideButtonTransition = true;

        private Action<RemoteListItem> _onClick;

        public RemoteListItem Item { get; private set; }
        public bool IsSelected { get; private set; }

        protected Button Button => _button;

        private void Awake()
        {
            if (_button == null) _button = GetComponent<Button>();
            if (_button == null) _button = GetComponentInChildren<Button>(true);
            if (_selectionGraphic == null && _button != null) _selectionGraphic = _button.targetGraphic;
            if (_overrideButtonTransition && _button != null) _button.transition = Selectable.Transition.None;
        }

        public virtual void Bind(RemoteListItem item, Sprite icon, Action<RemoteListItem> onClick)
        {
            Item = item;
            _onClick = onClick;

            if (_label != null) _label.text = item != null ? item.label : string.Empty;

            if (_sublabel != null)
            {
                string text = item != null ? item.sublabel : string.Empty;
                _sublabel.text = text ?? string.Empty;
                _sublabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }

            if (_icon != null)
            {
                _icon.sprite = icon;
                _icon.enabled = icon != null;
            }

            if (_button != null)
            {
                _button.onClick.RemoveListener(HandleClick);
                _button.onClick.AddListener(HandleClick);
            }

            SetSelected(false);
        }

        public virtual void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (_selectionGraphic != null) _selectionGraphic.color = selected ? _selectedColor : _unselectedColor;
        }

        public void SetInteractable(bool interactable)
        {
            if (_button != null) _button.interactable = interactable;
        }

        private void OnDestroy()
        {
            if (_button != null) _button.onClick.RemoveListener(HandleClick);
        }

        private void HandleClick()
        {
            if (Item != null) _onClick?.Invoke(Item);
        }
    }
}
