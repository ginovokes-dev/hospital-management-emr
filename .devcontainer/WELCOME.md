DANPHE EMR - HOW TO OPEN IT
===========================

1. The system is starting by itself right now.
   The FIRST time this takes 20-40 minutes. Later starts take about a minute.

2. To watch it work:  press  Cmd + Shift + P , type  Creation Log  and press Enter.
   When you see the words   Danphe EMR is ready   it is finished.
   (Click in this window now and then while you wait, so the codespace does not fall asleep.)

3. Then open the PORTS tab (at the bottom of the window) and click the little globe icon next to 8080.
   A new tab opens with the sign-in page.

4. Sign in with   username: admin    password: 123
   Change the password straight away:  top right > admin > My Profile > Change Password.

If something looks wrong, click the TERMINAL tab, paste this line and press Return:

    bash docker/start.sh --no-open

Full guide: START-HERE.md
