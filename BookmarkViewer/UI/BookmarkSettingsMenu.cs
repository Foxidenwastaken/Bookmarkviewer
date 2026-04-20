using System.ComponentModel;
using UnityEngine;
using Zenject;

namespace BookmarkViewer.UI
{
    public class BookmarkSettingsMenu : MonoBehaviour, IInitializable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Initialize()
        {
        }
    }
}