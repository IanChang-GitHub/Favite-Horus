private bool m_IsShowDlgDone = true;
public async void ShowDlg(bool _viewmode)
{
    if(m_IsShowDlgDone == false)
    {
        FVLog.Log("[ShowDlg] 等待AutoRun PP-Select 回應中..");
        return;
    }

    if (Visibility == Visibility.Visible)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        this.Activate();
        this.Focus();

        return;
    }

    GetViewModel().ViewMode = _viewmode;

    GetViewModel().ActiveProd   = null;
    GetViewModel().UserLotID    = null;
    GetViewModel().ActiveRecipe = null;

    GetViewModel().ReloadRcpList();
    GetViewModel().IsRcpComboBoxEnabled = true;

    GetViewModel().JobList.Clear();

    var recipeList = GetViewModel().RcpList.ToList();

    if (_viewmode)
    {
        GetViewModel().CanMap = false;
        GetViewModel().CanRun = false;
    }
    else
    {
        GetViewModel().CanMap = true;
        GetViewModel().CanRun = false;
    }

    FVLog.Log("等待PP-Select Reply...............................");

    m_IsShowDlgDone = false;
    string recipeCIM = string.Empty;

    var result = await DlgLoadProcessBox.RunAsync<bool>(
        title: "AutoRun",
        workThread: async (progress, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            progress.Report("Wait CIM Report UNLOAD COMPLETE");
            await TaskEx.Delay(500);
            bool? sucUnLoadComplete = HorusSystem.Instance.OnEqReportPort1TransferStateToCIM?.Invoke(PortTransferState.UNLOAD_COMPLETE);
            
            if((sucUnLoadComplete ?? false) == false)
            {
                FVLog.Log("CIM Report UNLOAD COMPLETE Fail!");
                return false;
            }

            ct.ThrowIfCancellationRequested();

            progress.Report("Wait CIM Report READY TO LOAD");
            await TaskEx.Delay(500);
            bool? sucReadLoad = HorusSystem.Instance.OnEqReportPort1TransferStateToCIM?.Invoke(PortTransferState.READY_TO_LOAD);
            if ((sucReadLoad ?? false) == false)
            {
                FVLog.Log("CIM Report READY TO LOAD Fail!");
                return false;
            }

            ct.ThrowIfCancellationRequested();

            progress.Report("Wait CIM PP-Select Reply");
            await TaskEx.Delay(500);

            //recipeCIM = HorusSystem.Instance.OnEQGetPPSelectRequestFromCIM?.Invoke();
            if (HorusSystem.Instance.FVCIM.TryGetPPSelectRecipe(out recipeCIM, out var recipeTime) == false)
            {
                FVLog.Log("[TryGetCIMRecipe] PP-Select Request 失敗");
                return false;
            }

            if ((DateTime.Now - recipeTime) > TimeSpan.FromMinutes(30) && (recipeCIM ?? string.Empty) == string.Empty)
            {
                FVLog.Log("[TryGetCIMRecipe] PP-Select Request 失敗, PP-Select 過期.");
                return false;
            }

            progress.Report("Check if the Recipe is valid");

            var found = recipeList.FirstOrDefault(item => item == recipeCIM); //尋找第一個符合條件
            if (found == null)
            {
                FVLog.Log("[TryGetCIMRecipe] 接收CIM發送不存在Recipe");
                return false;
            }

            ct.ThrowIfCancellationRequested();

            progress.Report("等待CIM處理完成!");

            return true;
        },
        totalCount: 5);

    m_IsShowDlgDone = true;

    if (result.IsCancelled == true)
    {                
        return;
    }
  

    if(result.IsSuccess == true && result.Value == true)
    {
        GetViewModel().ActiveRecipe = recipeCIM;
        GetViewModel().IsRcpComboBoxEnabled = false; //鎖定ComboBox

        FVLog.Log($"[TryGetCIMRecipe] PP-Select Request 成功，設定Recipe{recipeCIM}");
    }

    this.Show();
    Visibility = Visibility.Visible;
}