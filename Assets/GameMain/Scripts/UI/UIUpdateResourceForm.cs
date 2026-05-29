using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;
using GameFramework.Event;
using UnityEngine.UI;

namespace Flower
{
    public class UIUpdateResourceForm : MonoBehaviour
    {
        [SerializeField]
        private Text m_DescriptionText = null;

        [SerializeField]
        private Slider m_ProgressSlider = null;
        public void SetProgress(float progress, string description)
        {
            m_ProgressSlider.value = progress;
            m_DescriptionText.text = description;
        }
    }

}