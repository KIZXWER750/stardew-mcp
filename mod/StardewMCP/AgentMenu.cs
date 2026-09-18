using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

public sealed class AgentMenu : IClickableMenu
{
    private readonly IngameAgent agent;
    private readonly TextBox input;
    private readonly Rectangle submit;
    private readonly Rectangle reconnect;
    private readonly Rectangle clear;
    private readonly Rectangle goalPanel;
    private int goalScroll;
    private string lastGoalText="";
    public AgentMenu(IngameAgent agent) : base((Game1.uiViewport.Width-850)/2,(Game1.uiViewport.Height-620)/2,850,620,true)
    {
        this.agent=agent;
        goalPanel=new Rectangle(xPositionOnScreen+35,yPositionOnScreen+82,780,160);
        // Stardew's TextBox refuses additional characters when their rendered
        // width reaches Width. Keep it only as the IME-aware keyboard buffer
        // with a deliberately large hidden width; this menu renders and wraps
        // the visible text independently inside goalPanel.
        input=new TextBox(null,null,Game1.smallFont,Color.Black) {X=goalPanel.X+12,Y=goalPanel.Y+12,Width=1000000,Text=agent.LastGoal};
        submit=new Rectangle(xPositionOnScreen+35,yPositionOnScreen+257,220,52);
        reconnect=new Rectangle(xPositionOnScreen+285,yPositionOnScreen+257,220,52);
        clear=new Rectangle(xPositionOnScreen+535,yPositionOnScreen+257,220,52);
        input.Selected=true;Game1.keyboardDispatcher.Subscriber=input;
    }
    private void CloseInput() {input.Selected=false;Game1.keyboardDispatcher.Subscriber=null;exitThisMenu();}
    public override void receiveLeftClick(int x,int y,bool playSound=true)
    {
        if(upperRightCloseButton!=null && upperRightCloseButton.containsPoint(x,y)) {CloseInput();return;}
        if(submit.Contains(x,y)) {if(agent.Submit(input.Text)) CloseInput();return;}
        if(reconnect.Contains(x,y)) {agent.StartHost();return;}
        if(clear.Contains(x,y)) {
            input.Text="";lastGoalText="";goalScroll=0;agent.ClearGoalDraft();
            input.Selected=true;Game1.keyboardDispatcher.Subscriber=input;
            return;
        }
        if(goalPanel.Contains(x,y)) {input.Selected=true;Game1.keyboardDispatcher.Subscriber=input;return;}
        base.receiveLeftClick(x,y,playSound);
    }
    public override void receiveKeyPress(Keys key)
    {
        // TextBox handles typing. Enter deliberately never submits.
        if(key==Keys.Escape) CloseInput();
    }
    public override void receiveScrollWheelAction(int direction)
    {
        if(direction>0) goalScroll++;
        else if(direction<0) goalScroll=Math.Max(0,goalScroll-1);
        base.receiveScrollWheelAction(direction);
    }
    private static string[] WrappedLines(string text,int pixelWidth)
    {
        string wrapped=Game1.parseText((text??"").Replace("\r",""),Game1.smallFont,pixelWidth);
        return wrapped.Split('\n');
    }
    private static string VisibleLines(string text,int pixelWidth,int maxLines,bool tail)
    {
        string[] lines=WrappedLines(text,pixelWidth);
        if(lines.Length<=maxLines) return string.Join("\n",lines);
        if(tail) return "…\n"+string.Join("\n",lines,(lines.Length-(maxLines-1)),maxLines-1);
        return string.Join("\n",lines,0,maxLines-1)+"\n…";
    }
    private string VisibleGoal()
    {
        if(input.Text!=lastGoalText) {lastGoalText=input.Text;goalScroll=0;}
        string[] lines=WrappedLines(input.Text,goalPanel.Width-32);
        const int shown=3;
        int maxScroll=Math.Max(0,lines.Length-shown);
        goalScroll=Math.Min(goalScroll,maxScroll);
        int start=Math.Max(0,lines.Length-shown-goalScroll);
        int count=Math.Min(shown,lines.Length-start);
        return string.Join("\n",lines,start,count);
    }
    public override void draw(SpriteBatch b)
    {
        IClickableMenu.drawTextureBox(b,xPositionOnScreen,yPositionOnScreen,width,height,Color.White);
        b.DrawString(Game1.dialogueFont,"AI 목표 입력",new Vector2(xPositionOnScreen+35,yPositionOnScreen+30),Color.Black);
        IClickableMenu.drawTextureBox(b,goalPanel.X,goalPanel.Y,goalPanel.Width,goalPanel.Height,Color.White);
        string preview=VisibleGoal();
        if(input.Selected) preview+="|";
        b.DrawString(Game1.smallFont,preview,new Vector2(goalPanel.X+14,goalPanel.Y+12),Color.Black);
        string count=input.Text.Length+" / 4000자 · 마지막 3줄 표시 · 마우스 휠로 이전 줄 보기";
        b.DrawString(Game1.smallFont,count,new Vector2(goalPanel.X+14,goalPanel.Bottom-34),Color.Gray);
        IClickableMenu.drawTextureBox(b,submit.X,submit.Y,submit.Width,submit.Height,Color.White);
        b.DrawString(Game1.smallFont,"실행",new Vector2(submit.X+25,submit.Y+15),agent.Ready?Color.Black:Color.Gray);
        IClickableMenu.drawTextureBox(b,reconnect.X,reconnect.Y,reconnect.Width,reconnect.Height,Color.White);
        b.DrawString(Game1.smallFont,"서버 재연결",new Vector2(reconnect.X+20,reconnect.Y+15),Color.Black);
        IClickableMenu.drawTextureBox(b,clear.X,clear.Y,clear.Width,clear.Height,Color.White);
        b.DrawString(Game1.smallFont,"입력 지우기",new Vector2(clear.X+20,clear.Y+15),Color.Black);
        var statusBox=new Rectangle(xPositionOnScreen+35,yPositionOnScreen+324,780,78);
        IClickableMenu.drawTextureBox(b,statusBox.X,statusBox.Y,statusBox.Width,statusBox.Height,Color.White);
        b.DrawString(Game1.smallFont,VisibleLines(agent.Status,statusBox.Width-28,2,false),new Vector2(statusBox.X+14,statusBox.Y+10),Color.Black);
        var resultBox=new Rectangle(xPositionOnScreen+35,yPositionOnScreen+416,780,120);
        IClickableMenu.drawTextureBox(b,resultBox.X,resultBox.Y,resultBox.Width,resultBox.Height,Color.White);
        b.DrawString(Game1.smallFont,VisibleLines(agent.Result,resultBox.Width-28,3,false),new Vector2(resultBox.X+14,resultBox.Y+10),Color.DarkBlue);
        b.DrawString(Game1.smallFont,"취소 "+agent.Config.CancelKey+"   새 목표 "+agent.Config.OpenKey+"\n전체 결과: Mods/StardewMCP/logs",new Vector2(xPositionOnScreen+35,yPositionOnScreen+552),Color.Black);
        base.draw(b);drawMouse(b);
    }
}
