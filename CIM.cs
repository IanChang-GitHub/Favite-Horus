private CancellationTokenSource m_PPSelectMonitorCts;
private Task                    m_PPSelectMonitorTask;
private int                     m_MonitorState = 0;
private object                  m_PPSelectLock = new object();
private string                  m_TargetRecipe = string.Empty;
private bool                    m_HasNewRecipe = false;
private int                     m_LastPPSelectIndex = -1;
private DateTime                m_LastRecipeReceivedTime;

public bool IsPPSelectMonitorRunning => (m_PPSelectMonitorTask != null && m_PPSelectMonitorTask.IsCompleted == false);

public void StartPPSelectMonitor()
{
    // 用來防手賤 一直按
    if (Interlocked.CompareExchange(ref m_MonitorState, 1, 0) != 0) //(目標變數,新值,比較值)，合併成單一cpu指令，若變數跟比較值相等才會換成新值，交換成功會回傳舊值
    {
		return;
	}

	if (IsPPSelectMonitorRunning == true)
	{
		FVLog.Log("已處於監測 PP-Select 狀態中.");
		return;
	}

	try
	{
		m_PPSelectMonitorCts = new CancellationTokenSource(); //取消執行緒的遙控器
		m_PPSelectMonitorTask = Task.Run(() => PPSelectMonitor(m_PPSelectMonitorCts.Token), m_PPSelectMonitorCts.Token);
	}
	catch (Exception ex)
	{
		m_MonitorState = 0;

		FVLog.Log($"[StartPPSelectMonitor] Exception: {ex.Message}, StackTrace: {ex.StackTrace}.");
		return;
	}
}

public async void StopPPSelectMonitorAsync()
{
    // 用來防手賤 一直按
    if (Interlocked.CompareExchange(ref m_MonitorState, 0, 1) != 1)
	{
		return;
	}

	try
	{
		m_PPSelectMonitorCts?.Cancel();

		if (m_PPSelectMonitorTask != null)
		{
			try
			{
				await m_PPSelectMonitorTask;
			}
			catch (Exception ex)
			{
				throw new Exception($"Exception: {ex.Message}, StackTrace: {ex.StackTrace}.");
			}
			finally
			{
				m_PPSelectMonitorTask = null;
			}
		}

		m_PPSelectMonitorCts?.Dispose();
		m_PPSelectMonitorCts = null;

		FVLog.Log($"[StopPPSelectMonitorAsync] StopPPSelectMonitorAsync complete...");
	}
	catch (Exception ex)
	{
		FVLog.Log($"[StopPPSelectMonitorAsync] Exception: {ex.Message}, StackTrace: {ex.StackTrace}.");
		return;
	}
}

private async Task PPSelectMonitor(CancellationToken token)
{
	FVLog.Log("[PPSelectMonitor] Start PPSelectMonitor...");

	try
	{
		while (token.IsCancellationRequested == false) //IsCancellationRequested:檢查是否有取消
        {
			if (TryGetPPSelectRequestFromCIM(out var targetRecipe) == true)
			{
				lock (m_PPSelectLock)
				{
					m_TargetRecipe = targetRecipe;
					m_HasNewRecipe = true;
					m_LastRecipeReceivedTime = DateTime.Now;
				}

				FVLog.Log($"--- Get PPSelect Request RecipeID={targetRecipe} Request from CIM successfully ---");
			}

			await Task.Delay(100, token); //收到token立即取消，不等delay時間跑完才取消
		}
	}
	catch (Exception ex)
	{
		FVLog.Log($"[StartPPSelectMonitor] Exception: {ex.Message}, StackTrace: {ex.StackTrace}.");
		return;
	}
	finally
	{
		FVLog.Log("[PPSelectMonitor] PPSelectMonitor stopped.");
	}
}

private bool TryGetPPSelectRequestFromCIM(out string targetRecipe)
{
	targetRecipe = string.Empty;    

	if (EQMEM_PPSelectRequest == null || EQMEM_PPSelectRequest_Index == null || EQMEM_PPSelectRequest_Reply == null)
	{
		FVLog.Log("Error: PLCMemData instances for PPSelect Request (EQMEM_PPSelectRequest or EQMEM_PPSelectRequest_Index or EQMEM_PPSelectRequest_Reply) are not initialized.");
		return false;
	}

	if (GetDataByPLC(EQMEM_PPSelectRequest_Index, bRead: true) == false) //根據Cim提供的位子去PLC撈資料
	{
		FVLog.Log($"Error: Failed to read PPSelect Request Index from PLC (D{EQMEM_PPSelectRequest_Index.PosData}).");
		return false;
	}

	int currentPPSelectIndex = EQMEM_PPSelectRequest_Index.BitValue;

	// 外面再拿的時候 不確定會不會 race 加一下 視情況拿掉 
	lock (m_PPSelectLock)
	{
		if (currentPPSelectIndex == m_LastPPSelectIndex) 
		{
			return false; // 直接下一輪了吧
		}
	}

	if (GetDataByPLC(EQMEM_PPSelectRequest, bRead: true) == false)
	{
		FVLog.Log($"Error: Failed to read PPSelect Request from PLC (D{EQMEM_PPSelectRequest.PosData}).");
		return false;
	}

	//PPID RecipeID
	const int recipeIDDataLength = 15;
	if (PLCStringDataDecode(EQMEM_PPSelectRequest.ListValue.GetRange(0, recipeIDDataLength), out string outTargetRecipe) == false)
	{
		FVLog.Log($"Error: Failed to decode PPSelect Request Data");
		return false;
	}

	if (GetDataByPLC(EQMEM_PPSelectRequest_Reply, bRead: true) == false)
	{
		FVLog.Log($"Error: Failed to read PPSelect Request Reply from PLC (D{EQMEM_PPSelectRequest_Reply.PosData}).");
		return false;
	}

	EQMEM_PPSelectRequest_Reply.BitValue = (EQMEM_PPSelectRequest_Reply.BitValue + 1) % 10000;
	if (WriteDataToPLC(EQMEM_PPSelectRequest_Reply) == false)
	{
		FVLog.Log($"Error: Failed to write PPSelect Request Reply to PLC (D{EQMEM_PPSelectRequest_Reply.PosData}).");
		return false;
	}

	// 一樣加一下保障
	lock (m_PPSelectLock)
	{
		m_LastPPSelectIndex = currentPPSelectIndex;
		targetRecipe = outTargetRecipe;
	}

	FVLog.Log($"--- Get PPSelect Request RecipeID={outTargetRecipe} Request from CIM successfully ---");
	return true;
}

// 有新的recipe 取過 就會被清掉唷~
public bool TryGetPPSelectRecipe(out string targetRecipe, out DateTime recipeTime)
{
	lock(m_PPSelectLock)
	{
		if (m_HasNewRecipe == false)
		{
			targetRecipe = string.Empty;
			recipeTime = default;
			return false;
		}

		targetRecipe = m_TargetRecipe;
		recipeTime   = m_LastRecipeReceivedTime;
		m_HasNewRecipe = false;

		return true;
	}
}