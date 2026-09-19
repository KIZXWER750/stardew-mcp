using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

public sealed class AgentQuestionMenu : IClickableMenu
{
    private readonly IngameAgent agent;
    private readonly AgentQuestion question;
    private readonly TextBox input;
    private readonly Rectangle answerPanel;
    private readonly Rectangle submit;

    public AgentQuestionMenu(IngameAgent agent, AgentQuestion question)
        : base((Game1.uiViewport.Width - 820) / 2, (Game1.uiViewport.Height - 560) / 2, 820, 560, true)
    {
        this.agent = agent; this.question = question;
        answerPanel = new Rectangle(xPositionOnScreen + 35, yPositionOnScreen + 300, 750, 120);
        input = new TextBox(null, null, Game1.smallFont, Color.Black) { X = answerPanel.X + 12, Y = answerPanel.Y + 12, Width = 1000000 };
        submit = new Rectangle(xPositionOnScreen + 285, yPositionOnScreen + 440, 250, 58);
        input.Selected = true; Game1.keyboardDispatcher.Subscriber = input;
    }

    private void CloseInput() { input.Selected = false; Game1.keyboardDispatcher.Subscriber = null; exitThisMenu(); }
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y)) { CloseInput(); return; }
        if (answerPanel.Contains(x, y)) { input.Selected = true; Game1.keyboardDispatcher.Subscriber = input; return; }
        if (submit.Contains(x, y) && agent.SubmitAgentAnswer(question.Id, input.Text)) { CloseInput(); return; }
        base.receiveLeftClick(x, y, playSound);
    }
    public override void receiveKeyPress(Keys key) { if (key == Keys.Escape) CloseInput(); }
    private static string Lines(string text, int width, int max)
    {
        string[] lines = Game1.parseText((text ?? "").Replace("\r", ""), Game1.smallFont, width).Split('\n');
        return lines.Length <= max ? string.Join("\n", lines) : string.Join("\n", lines, 0, max - 1) + "\n…";
    }
    public override void draw(SpriteBatch b)
    {
        IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        b.DrawString(Game1.dialogueFont, "AI 추가 질문", new Vector2(xPositionOnScreen + 35, yPositionOnScreen + 28), Color.Black);
        b.DrawString(Game1.smallFont, Lines(question.Prompt, 735, 5), new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 92), Color.Black);
        string options = question.Options.Count == 0 ? "직접 답변을 입력하세요." : "선택 예시: " + string.Join("  ·  ", question.Options);
        b.DrawString(Game1.smallFont, Lines(options, 735, 2), new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 220), Color.DarkSlateBlue);
        IClickableMenu.drawTextureBox(b, answerPanel.X, answerPanel.Y, answerPanel.Width, answerPanel.Height, Color.White);
        b.DrawString(Game1.smallFont, Lines(input.Text + (input.Selected ? "|" : ""), answerPanel.Width - 28, 3), new Vector2(answerPanel.X + 14, answerPanel.Y + 12), Color.Black);
        b.DrawString(Game1.smallFont, input.Text.Length + " / 2000자", new Vector2(answerPanel.X + 14, answerPanel.Bottom - 32), Color.Gray);
        IClickableMenu.drawTextureBox(b, submit.X, submit.Y, submit.Width, submit.Height, Color.White);
        b.DrawString(Game1.smallFont, "답변하고 작업 계속", new Vector2(submit.X + 28, submit.Y + 18), agent.Ready ? Color.Black : Color.Gray);
        b.DrawString(Game1.smallFont, "Esc로 닫아도 F6으로 질문창을 다시 열 수 있습니다.", new Vector2(xPositionOnScreen + 135, yPositionOnScreen + 515), Color.Gray);
        base.draw(b); drawMouse(b);
    }
}
