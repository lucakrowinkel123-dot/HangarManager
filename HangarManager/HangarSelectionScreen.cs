using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;

namespace HangarManager
{
    internal sealed class HangarChoice
    {
        public HangarChoice(long slot, string name)
        {
            Slot = slot;
            Name = string.IsNullOrWhiteSpace(name) ? "Stored Ship " + slot : name;
        }

        public long Slot
        {
            get;
        }
        public string Name
        {
            get;
        }
    }

    internal sealed class HangarSelectionScreen : MyGuiScreenBase
    {
        private readonly string hangarName;
        private readonly IReadOnlyList<HangarChoice> choices;
        private readonly Action<long> deploy;
        public HangarSelectionScreen(string hangarName, IReadOnlyList<HangarChoice> choices, Action<long> deploy) : base(
            position: new Vector2(0.5f),
            backgroundColor: MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            size: new Vector2(0.46f, Math.Min(0.82f, 0.22f + choices.Count * 0.055f)),
            isTopMostScreen: true,
            backgroundTexture: null,
            backgroundTransition: MySandboxGame.Config.UIBkOpacity,
            guiTransition: MySandboxGame.Config.UIOpacity,
            gamepadSlot: null)
        {
            this.hangarName = hangarName;
            this.choices = choices;
            this.deploy = deploy;
            EnabledBackgroundFade = true;
            m_closeOnEsc = true;
            CanHideOthers = true;
            CanBeHidden = true;
            CloseButtonEnabled = true;
        }

        public override string GetFriendlyName() => typeof(HangarSelectionScreen).FullName;
        public override void LoadContent()
        {
            base.LoadContent();
            RecreateControls(true);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption(text: "Deploy from " + hangarName, captionScale: 0.8f);
            var y = -m_size.Value.Y / 2f + 0.13f;
            foreach (var choice in choices)
            {
                var selected = choice;
                var button = new MyGuiControlButton(position: new Vector2(0f, y), text: new StringBuilder(selected.Name), onButtonClick: _ => SelectAndClose(selected.Slot));
                button.Size = new Vector2(0.38f, 0.04f);
                Controls.Add(button);
                y += 0.055f;
            }
        }

        private void SelectAndClose(long slot)
        {
            CloseScreen();
            deploy(slot);
        }
    }
}
