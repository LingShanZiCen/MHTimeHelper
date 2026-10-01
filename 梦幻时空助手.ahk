; AutoHotkey v2.0 脚本 - 梦幻西游智能助手（日志版 v3.2）

#Requires AutoHotkey v2.0
#SingleInstance Force
SetWorkingDir A_ScriptDir

; ========== 日志功能 ==========
LogContent := ""
Log(msg) {
    global LogContent
    LogContent .= A_Now " - " msg "`n"
}
Log("=== 梦幻助手启动 ===")
Log("时间: " A_Now)
Log("")

; ========== 自动请求管理员权限 ==========
if !A_IsAdmin {
    Log("非管理员权限，尝试提权...")
    try {
        Run("*RunAs " A_ScriptFullPath)
        Log("提权成功，退出当前实例")
        ExitApp
    } catch as err {
        Log("提权失败: " err.Message)
    }
}

; ========== 界面配置 ==========
MyGui := Gui()
MyGui.Title := "梦幻西游智能助手"
MyGui.SetFont("s10", "Microsoft YaHei")
MyGui.OnEvent("Close", OnGuiClose)

; 游戏路径
MyGui.Add("Text", , "游戏路径：")
edGamePath := MyGui.Add("Edit", "w400 vGamePath")

; 窗口标题关键词
MyGui.Add("Text", , "窗口标题关键词：")
edWinTitle := MyGui.Add("Edit", "w400 vWinTitle")

; 启动数量
MyGui.Add("Text", , "启动数量：")
ddCount := MyGui.Add("DropDownList", "w80 vCount", ["1","2","3","4","5"])
ddCount.Choose(5)

; ---------- 分辨率设置 ----------
MyGui.Add("Text", "xs y+2", "分辨率模式：")
ddResMode := MyGui.Add("DropDownList", "w150 vResMode", ["自定义宽高", "16:9 预设分辨率"])
ddResMode.OnEvent("Change", OnResModeChange)

; 自定义宽高
MyGui.Add("Text", "xs y+2", "排列宽度：")
edWinWidth := MyGui.Add("Edit", "x+5 w80 vWinWidth", "800")
MyGui.Add("Text", "x+15 yp", "高度：")
edWinHeight := MyGui.Add("Edit", "x+5 w80 vWinHeight", "600")

; 预设分辨率下拉（初始隐藏）
MyGui.Add("Text", "xs y+2 vPresetLabel", "选择分辨率：")
ddPreset := MyGui.Add("DropDownList", "x+5 w150 vPreset", ["2560x1440", "1920x1080", "1600x900", "1280x720"])
ddPreset.Visible := false
MyGui["PresetLabel"].Visible := false

; 启动后等待（秒）
MyGui.Add("Text", "xs y+2", "启动后等待（秒）：")
edWaitSec := MyGui.Add("Edit", "w80 vWaitSec", "7")

; 同步器路径
MyGui.Add("Text", "xs y+10", "同步器路径：")
edSyncPath := MyGui.Add("Edit", "w400 vSyncPath")

; ---------- 按钮区域（第一行） ----------
btnLaunch := MyGui.Add("Button", "w100", "一键启动")
btnLaunch.OnEvent("Click", Launch)
btnLaunch.Opt("Default")

MyGui.Add("Button", "x+10 w100", "打开同步器").OnEvent("Click", OpenSync)
MyGui.Add("Button", "x+10 w100", "保存配置").OnEvent("Click", SaveConfig)
btnGetSize := MyGui.Add("Button", "x+10 w120", "获取窗口大小")
btnGetSize.OnEvent("Click", GetActiveWindowSize)
btnGetSize.ToolTip := "快捷键：Ctrl+Win+R"

; ---------- 按钮区域（第二行） ----------
MyGui.Add("Button", "xs y+10 w100", "前置全部").OnEvent("Click", BringAllGamesToFront)
MyGui.Add("Button", "x+10 w100", "老板键").OnEvent("Click", MinimizeAllGames)
; ----- 导出日志按钮 -----
MyGui.Add("Button", "x+10 w100", "导出日志").OnEvent("Click", ExportLog)

; ---------- 快捷键设置 ----------
MyGui.Add("Text", "xs y+18 cGray", "快捷键设置（点“设置”后按下组合键，两键或多键组合都行）")
MyGui.Add("Text", "xs y+4 w90", "前置全部：")
edHotkeyFront := MyGui.Add("Edit", "x+5 w130 ReadOnly", "^#T")
btnHotkeyFront := MyGui.Add("Button", "x+8 w70", "设置")
btnHotkeyFront.OnEvent("Click", (*) => StartRecord("front"))
MyGui.Add("Text", "xs y+4 w90", "老板键：")
edHotkeyMin := MyGui.Add("Edit", "x+5 w130 ReadOnly", "^#M")
btnHotkeyMin := MyGui.Add("Button", "x+8 w70", "设置")
btnHotkeyMin.OnEvent("Click", (*) => StartRecord("min"))
MyGui.Add("Text", "xs y+4 w90", "获取窗口大小：")
edHotkeySize := MyGui.Add("Edit", "x+5 w130 ReadOnly", "Ctrl+Win+R")
btnHotkeySize := MyGui.Add("Button", "x+8 w70", "设置")
btnHotkeySize.OnEvent("Click", (*) => StartRecord("size"))

; 署名
MyGui.Add("Text", "x10 y+25 cGray", "作者：楪铃  版本：v3.2")

; 显示界面（窗口可自由拖动缩放）
MyGui.Opt("+Resize +MinSize630x620")
MyGui.Show("w630 h700")
Log("界面加载完成")

; ---------- 托盘菜单中文化 ----------
A_TrayMenu.Delete()
A_TrayMenu.Add("显示主界面", (*) => MyGui.Show())
A_TrayMenu.Add("退出软件", (*) => ExitApp())
A_TrayMenu.Default := "显示主界面"
A_TrayMenu.ClickCount := 1

; ---------- 关闭窗口时的选择弹窗 ----------
OnGuiClose(*) {
    global closeGui
    closeGui := Gui("+AlwaysOnTop +Owner" MyGui.Hwnd)
    closeGui.Title := "梦幻时空助手"
    closeGui.SetFont("s10", "Microsoft YaHei")
    closeGui.Add("Text", , "要彻底退出软件，还是最小化到系统托盘？")
    bExit := closeGui.Add("Button", "w110 Default", "彻底退出")
    bMin := closeGui.Add("Button", "x+10 w110", "最小化")
    bCancel := closeGui.Add("Button", "x+10 w110", "取消")
    bExit.OnEvent("Click", (*) => DoCloseChoice("exit"))
    bMin.OnEvent("Click", (*) => DoCloseChoice("min"))
    bCancel.OnEvent("Click", (*) => DoCloseChoice("cancel"))
    closeGui.Show("w390 h90")
}

DoCloseChoice(choice) {
    global closeGui
    closeGui.Destroy()
    if (choice = "exit") {
        Log("用户选择彻底退出")
        ExitApp()
    } else if (choice = "min") {
        Log("用户选择最小化到托盘")
        MyGui.Hide()
    }
}

; ========== 辅助函数 ==========
; ---------- 快捷键录制 ----------
hkFront := "^#T"
hkMin := "^#M"
hkSize := "^#R"
recordingFor := ""

; ---------- 快捷键格式转换 ----------
HotkeyToFriendly(hk) {
    if (hk = "")
        return ""
    mods := ""
    while (hk != "") {
        c := SubStr(hk, 1, 1)
        if (c = "^")
            mods .= "Ctrl+"
        else if (c = "#")
            mods .= "Win+"
        else if (c = "!")
            mods .= "Alt+"
        else if (c = "+")
            mods .= "Shift+"
        else
            break
        hk := SubStr(hk, 2)
    }
    return mods hk
}

FriendlyToHotkey(f) {
    if (f = "")
        return ""
    ; 已是符号格式（以 ^#!+ 开头）则原样返回
    if RegExMatch(f, "^[\^#!+]")
        return f
    s := f
    s := StrReplace(s, "Ctrl+", "^")
    s := StrReplace(s, "Win+", "#")
    s := StrReplace(s, "Alt+", "!")
    s := StrReplace(s, "Shift+", "+")
    ; 兼容小写
    s := StrReplace(s, "ctrl+", "^")
    s := StrReplace(s, "win+", "#")
    s := StrReplace(s, "alt+", "!")
    s := StrReplace(s, "shift+", "+")
    return s
}

StartRecord(which) {
    global recordingFor
    if (recordingFor != "")
        CancelRecord()
    recordingFor := which
    if (which = "front") {
        btnHotkeyFront.Text := "按组合键... Esc取消"
        edHotkeyFront.Focus()
    } else if (which = "min") {
        btnHotkeyMin.Text := "按组合键... Esc取消"
        edHotkeyMin.Focus()
    } else if (which = "size") {
        btnHotkeySize.Text := "按组合键... Esc取消"
        edHotkeySize.Focus()
    }
    ; 10秒无操作自动取消录制，防止状态卡死
    SetTimer(RecordTimeout, -10000)
}

CancelRecord() {
    global recordingFor
    recordingFor := ""
    SetTimer(RecordTimeout, 0)
    btnHotkeyFront.Text := "设置"
    btnHotkeyMin.Text := "设置"
    btnHotkeySize.Text := "设置"
}

RecordTimeout() {
    global recordingFor
    if (recordingFor != "")
        CancelRecord()
}

; 键盘监听：仅在录制快捷键时生效；平时返回值必须为空字符串，绝不拦截正常输入
OnMessage(0x0100, WM_KEYDOWN)
OnMessage(0x0104, WM_KEYDOWN)

WM_KEYDOWN(wParam, lParam, msg, hwnd) {
    global recordingFor
    if (recordingFor = "")
        return ""
    keyName := GetKeyName(Format("vk{:02X}", wParam))
    if (keyName = "")
        return ""
    if (keyName = "Tab")
        return 1
    if InStr("Control Alt Shift Win LControl RControl LAlt RAlt LShift RShift LWin RWin", keyName)
        return ""
    if (keyName = "Escape") {
        CancelRecord()
        return 1
    }
    mods := ""
    if GetKeyState("Ctrl")
        mods .= "^"
    if GetKeyState("Alt")
        mods .= "!"
    if GetKeyState("Shift")
        mods .= "+"
    if GetKeyState("LWin") || GetKeyState("RWin")
        mods .= "#"
    hk := mods keyName
    conflict := ""
    if (recordingFor != "front" && hk = FriendlyToHotkey(edHotkeyFront.Value))
        conflict := "前置全部"
    else if (recordingFor != "min" && hk = FriendlyToHotkey(edHotkeyMin.Value))
        conflict := "老板键"
    else if (recordingFor != "size" && hk = FriendlyToHotkey(edHotkeySize.Value))
        conflict := "获取窗口大小"
    if (conflict != "") {
        MsgBox("这个组合键已经用在“" conflict "”上了，请换个组合。", "提示")
        return 1
    }
    if (recordingFor = "front")
        edHotkeyFront.Value := HotkeyToFriendly(hk)
    else if (recordingFor = "min")
        edHotkeyMin.Value := HotkeyToFriendly(hk)
    else if (recordingFor = "size")
        edHotkeySize.Value := HotkeyToFriendly(hk)
    CancelRecord()
    RegisterHotkeys()
    Log("快捷键已更新: " HotkeyToFriendly(hk))
    return 1
}

OnResModeChange(*) {
    if ddResMode.Text = "自定义宽高" {
        edWinWidth.Visible := true
        edWinHeight.Visible := true
        MyGui["PresetLabel"].Visible := false
        ddPreset.Visible := false
    } else {
        edWinWidth.Visible := false
        edWinHeight.Visible := false
        MyGui["PresetLabel"].Visible := true
        ddPreset.Visible := true
    }
}

GetEffectiveSize(&width, &height) {
    MyGui.Submit(false)
    if ddResMode.Text = "自定义宽高" {
        width := edWinWidth.Text
        height := edWinHeight.Text
        if !IsNumber(width) || width <= 0
            width := 800
        if !IsNumber(height) || height <= 0
            height := 600
    } else {
        preset := ddPreset.Text
        if InStr(preset, "x") {
            parts := StrSplit(preset, "x")
            if parts.Length = 2 {
                width := parts[1]
                height := parts[2]
            } else {
                width := 1920
                height := 1080
            }
        } else {
            width := 1920
            height := 1080
        }
        width := Integer(width)
        height := Integer(height)
    }
}

IsInArray(arr, hwnd) {
    for v in arr {
        if v = hwnd
            return true
    }
    return false
}

; ========== 一键启动 ==========
Launch(*) {
    MyGui.Submit(false)
    gamePath := edGamePath.Text
    count := ddCount.Text
    winTitle := edWinTitle.Text
    Log("一键启动: 目标数量=" count)
    if (winTitle = "") {
        winTitle := "梦幻西游"
        Log("关键词为空，使用默认: 梦幻西游")
    }
    
    waitSec := edWaitSec.Text
    if !IsNumber(waitSec) || waitSec <= 0
        waitSec := 7
    waitMs := waitSec * 1000
    Log("等待时间: " waitSec " 秒")

    if !FileExist(gamePath) {
        Log("错误: 游戏路径无效 - " gamePath)
        MsgBox("游戏路径无效，请填写正确的路径。", "错误", "IconX")
        return
    }

    selfHwnd := A_ScriptHwnd
    beforeWindows := []
    allBefore := WinGetList()
    for hwnd in allBefore {
        if (hwnd = selfHwnd)
            continue
        title := WinGetTitle(hwnd)
        if InStr(title, "智能助手")
            continue
        if InStr(title, winTitle) {
            beforeWindows.Push(hwnd)
        }
    }
    beforeSet := Map()
    for hwnd in beforeWindows {
        beforeSet[hwnd] := true
    }

    orderedWindows := []

    Loop count {
        Log("启动第 " A_Index " 个窗口...")
        Run(gamePath)
        startTime := A_TickCount
        newHwnd := 0
        while (A_TickCount - startTime < 10000) {
            allNow := WinGetList()
            for hwnd in allNow {
                if (hwnd = selfHwnd)
                    continue
                title := WinGetTitle(hwnd)
                if InStr(title, "智能助手")
                    continue
                if InStr(title, winTitle) && !beforeSet.Has(hwnd) {
                    newHwnd := hwnd
                    break
                }
            }
            if newHwnd {
                orderedWindows.Push(newHwnd)
                beforeSet[newHwnd] := true
                break
            }
            Sleep(200)
        }
        if !newHwnd {
            Log("警告: 第 " A_Index " 个窗口启动超时")
            MsgBox("第 " A_Index " 个窗口启动超时，请检查游戏是否正常。", "警告")
        }
        Sleep(500)
    }

    Log("等待 " waitSec " 秒让窗口稳定...")
    Sleep(waitMs)

    ; 补充遗漏窗口
    allNow := WinGetList()
    for hwnd in allNow {
        if (hwnd = selfHwnd)
            continue
        title := WinGetTitle(hwnd)
        if InStr(title, "智能助手")
            continue
        if InStr(title, winTitle) && !IsInArray(orderedWindows, hwnd) {
            orderedWindows.Push(hwnd)
            beforeSet[hwnd] := true
            Log("补充捕获遗漏窗口: " title)
        }
    }

    if orderedWindows.Length = 0 {
        Log("错误: 没有找到任何游戏窗口")
        MsgBox("没有找到任何游戏窗口。", "错误", "IconX")
        return
    }

    GetEffectiveSize(&winWidth, &winHeight)
    Log("排列尺寸: " winWidth "x" winHeight)

    actualCount := orderedWindows.Length
    if (actualCount = 5) {
        ArrangeFiveWindows(orderedWindows, winWidth, winHeight)
    } else {
        ArrangeGrid(orderedWindows, 2, winWidth, winHeight)
    }
    Log("一键启动完成，实际启动 " orderedWindows.Length " 个窗口")
    MsgBox("已启动 " orderedWindows.Length " 个游戏窗口，并完成布局。", "完成")
}

ArrangeFiveWindows(windows, winWidth, winHeight) {
    Log("执行5窗口排列...")
    MonitorGetWorkArea(1, &left, &top, &right, &bottom)
    screenWidth := right - left
    screenHeight := bottom - top

    x1 := left
    y1 := top
    x2 := left + (screenWidth - winWidth) // 2
    y2 := top
    x3 := right - winWidth
    y3 := top
    x4 := left
    y4 := bottom - winHeight
    x5 := right - winWidth
    y5 := bottom - winHeight

    positions := [[x1, y1], [x2, y2], [x3, y3], [x4, y4], [x5, y5]]

    for i, hwnd in windows {
        if i > positions.Length
            break
        x := positions[i][1]
        y := positions[i][2]
        try {
            WinMove(x, y, winWidth, winHeight, "ahk_id " hwnd)
            Sleep(50)
        } catch as err {
            Log("移动窗口失败 (索引 " i "): " err.Message)
        }
    }
}

ArrangeGrid(windows, cols, winWidth, winHeight) {
    Log("执行网格排列...")
    MonitorGetWorkArea(1, &left, &top, &right, &bottom)
    screenWidth := right - left
    screenHeight := bottom - top

    maxCols := Floor(screenWidth / winWidth)
    if (cols > maxCols)
        cols := maxCols
    if (cols < 1)
        cols := 1

    for index, hwnd in windows {
        row := (index - 1) // cols
        col := Mod(index - 1, cols)
        x := left + col * winWidth
        y := top + row * winHeight
        if (y + winHeight > bottom) {
            y := bottom - winHeight
        }
        try {
            WinMove(x, y, winWidth, winHeight, "ahk_id " hwnd)
            Sleep(50)
        } catch as err {
            Log("移动窗口失败 (索引 " index "): " err.Message)
        }
    }
}

; ========== 前置全部 ==========
BringAllGamesToFront(*) {
    MyGui.Submit(false)
    winTitle := edWinTitle.Text
    if (winTitle = "")
        winTitle := "梦幻西游"
    Log("前置全部: 关键词=" winTitle)

    all := WinGetList()
    count := 0
    lastHwnd := 0
    for hwnd in all {
        if (hwnd = A_ScriptHwnd)
            continue
        title := WinGetTitle(hwnd)
        if InStr(title, "智能助手")
            continue
        if InStr(title, winTitle) {
            if WinGetMinMax(hwnd) = -1
                WinRestore(hwnd)
            WinActivate(hwnd)
            Sleep(50)
            count++
            lastHwnd := hwnd
        }
    }
    if lastHwnd
        WinActivate(lastHwnd)
    if count > 0 {
        Log("前置全部: 找到 " count " 个窗口")
        MsgBox("已将 " count " 个游戏窗口带到最前。", "前置")
    } else {
        Log("前置全部: 未找到窗口")
        MsgBox("没有找到任何游戏窗口。", "前置")
    }
}

; ========== 老板键 ==========
MinimizeAllGames(*) {
    MyGui.Submit(false)
    winTitle := edWinTitle.Text
    if (winTitle = "")
        winTitle := "梦幻西游"
    Log("老板键: 关键词=" winTitle)

    all := WinGetList()
    count := 0
    for hwnd in all {
        if (hwnd = A_ScriptHwnd)
            continue
        title := WinGetTitle(hwnd)
        if InStr(title, "智能助手")
            continue
        if InStr(title, winTitle) {
            WinMinimize(hwnd)
            count++
        }
    }
    Log("老板键: 最小化 " count " 个窗口")
    MsgBox("已最小化 " count " 个游戏窗口。", "老板键")
}

; ========== 获取窗口大小（无需激活） ==========
GetActiveWindowSize(*) {
    MyGui.Submit(false)
    if ddResMode.Text != "自定义宽高" {
        Log("获取窗口大小失败: 当前为预设模式")
        MsgBox("当前处于预设分辨率模式，如需获取窗口大小，请先切换到“自定义宽高”模式。", "提示")
        return
    }
    winTitle := edWinTitle.Text
    if (winTitle = "")
        winTitle := "梦幻西游"
    Log("获取窗口大小: 关键词=" winTitle)

    all := WinGetList()
    matchedHwnd := 0
    matchedTitle := ""
    for hwnd in all {
        if (hwnd = A_ScriptHwnd)
            continue
        title := WinGetTitle(hwnd)
        if InStr(title, "智能助手")
            continue
        if InStr(title, winTitle) {
            matchedHwnd := hwnd
            matchedTitle := title
            break
        }
    }

    if !matchedHwnd {
        Log("获取窗口大小失败: 未找到窗口")
        MsgBox("没有找到任何标题包含 '" winTitle "' 的游戏窗口。`n请检查关键词是否正确。", "提示")
        return
    }

    WinGetPos(&x, &y, &width, &height, "ahk_id " matchedHwnd)
    if (width > 0 && height > 0) {
        edWinWidth.Value := width
        edWinHeight.Value := height
        Log("获取窗口大小成功: " matchedTitle " " width "x" height)
        MsgBox("已获取窗口 '" matchedTitle "' 的分辨率：`n宽度 = " width " 像素`n高度 = " height " 像素`n已自动填入窗口宽高输入框。", "成功")
    } else {
        Log("获取窗口大小失败: 窗口尺寸为0")
        MsgBox("无法获取窗口尺寸，可能窗口未完全加载或处于最小化状态。", "错误")
    }
}

; ========== 同步器 ==========
OpenSync(*) {
    MyGui.Submit(false)
    syncPath := edSyncPath.Text
    Log("打开同步器: " syncPath)
    if !FileExist(syncPath) {
        Log("同步器路径无效")
        MsgBox("同步器路径无效，请填写正确的路径。", "错误", "IconX")
        return
    }
    try {
        Run(syncPath)
        Log("同步器已打开")
        MsgBox("已打开同步器。", "提示")
    } catch as err {
        Log("打开同步器失败: " err.Message)
        MsgBox("打开同步器失败: " err.Message, "错误", "IconX")
    }
}

; ========== 保存配置 ==========
SaveConfig(*) {
    MyGui.Submit(false)
    Log("保存配置...")
    IniWrite(edGamePath.Text, "config.ini", "Settings", "GamePath")
    IniWrite(edWinTitle.Text, "config.ini", "Settings", "WinTitle")
    IniWrite(ddCount.Text, "config.ini", "Settings", "LastCount")
    IniWrite(edSyncPath.Text, "config.ini", "Settings", "SyncPath")
    IniWrite(edWinWidth.Text, "config.ini", "Settings", "WinWidth")
    IniWrite(edWinHeight.Text, "config.ini", "Settings", "WinHeight")
    IniWrite(edWaitSec.Text, "config.ini", "Settings", "WaitSec")
    IniWrite(ddResMode.Text, "config.ini", "Settings", "ResMode")
    IniWrite(ddPreset.Text, "config.ini", "Settings", "ResPreset")
    IniWrite(edHotkeyFront.Value, "config.ini", "Hotkeys", "Front")
    IniWrite(edHotkeyMin.Value, "config.ini", "Hotkeys", "Minimize")
    IniWrite(edHotkeySize.Value, "config.ini", "Hotkeys", "GetSize")
    Log("配置保存完成")
    MsgBox("所有配置已保存到 config.ini", "保存成功")
}

; ========== 加载配置 ==========
LoadConfig() {
    if !FileExist("config.ini") {
        Log("配置文件不存在，使用默认值")
        return
    }
    Log("加载配置...")
    ; 默认值
    gamePath := ""
    winTitle := ""
    lastCount := ""
    syncPath := ""
    winWidth := "800"
    winHeight := "600"
    waitSec := "7"
    resMode := "自定义宽高"
    resPreset := "1920x1080"

    try gamePath := IniRead("config.ini", "Settings", "GamePath", "")
    try winTitle := IniRead("config.ini", "Settings", "WinTitle", "")
    try lastCount := IniRead("config.ini", "Settings", "LastCount", "")
    try syncPath := IniRead("config.ini", "Settings", "SyncPath", "")
    try winWidth := IniRead("config.ini", "Settings", "WinWidth", "800")
    try winHeight := IniRead("config.ini", "Settings", "WinHeight", "600")
    try waitSec := IniRead("config.ini", "Settings", "WaitSec", "7")
    try resMode := IniRead("config.ini", "Settings", "ResMode", "自定义宽高")
    try resPreset := IniRead("config.ini", "Settings", "ResPreset", "1920x1080")

    if (gamePath != "")
        edGamePath.Value := gamePath
    if (winTitle != "")
        edWinTitle.Value := winTitle
    if (lastCount != "") {
        if (lastCount >= 1 && lastCount <= 5)
            ddCount.Choose(lastCount)
        else
            ddCount.Choose(5)
    }
    if (syncPath != "")
        edSyncPath.Value := syncPath
    if (winWidth != "")
        edWinWidth.Value := winWidth
    if (winHeight != "")
        edWinHeight.Value := winHeight
    if (waitSec != "")
        edWaitSec.Value := waitSec
    if (resMode = "自定义宽高") {
        ddResMode.Choose(1)
    } else {
        ddResMode.Choose(2)
    }
    ; 预设分辨率索引
    presetOptions := ["2560x1440", "1920x1080", "1600x900", "1280x720"]
    presetIndex := 0
    for i, v in presetOptions {
        if v = resPreset {
            presetIndex := i
            break
        }
    }
    if presetIndex
        ddPreset.Choose(presetIndex)
    else
        ddPreset.Choose(2)
    ; 快捷键（兼容旧配置中的 AHK 符号格式，显示统一为友好格式）
    edHotkeyFront.Value := HotkeyToFriendly(IniRead("config.ini", "Hotkeys", "Front", "^#T"))
    edHotkeyMin.Value := HotkeyToFriendly(IniRead("config.ini", "Hotkeys", "Minimize", "^#M"))
    edHotkeySize.Value := HotkeyToFriendly(IniRead("config.ini", "Hotkeys", "GetSize", "^#R"))
    OnResModeChange()
    Log("配置加载完成")
}

; ========== 导出日志 ==========
ExportLog(*) {
    global LogContent
    if (LogContent = "") {
        MsgBox("没有日志内容可导出。", "提示")
        return
    }
    
    ; 默认目录：脚本所在目录，默认文件名：带时间戳
    defaultDir := A_ScriptDir
    ; 生成默认文件名（如：梦幻助手日志_20250501_123456.txt）
    ; AHK v2 中 FormatTime 直接返回格式化后的字符串
    fileName := FormatTime(A_Now, "梦幻助手日志_yyyyMMdd_HHmmss")
    defaultFile := defaultDir "\" fileName
    
    ; 弹出保存对话框，默认目录为脚本目录
    selected := FileSelect("S", defaultFile, "保存日志为", "文本文件(*.txt)")
    if (selected = "") {
        return  ; 用户取消
    }
    
    ; 确保以 .txt 结尾
    if !InStr(selected, ".txt", , -1) {
        selected .= ".txt"
    }
    
    ; 写入文件
    try {
        ; 如果文件已存在，先删除（FileAppend 会覆盖，但先删除更安全）
        if FileExist(selected) {
            FileDelete(selected)
        }
        FileAppend(LogContent, selected, "UTF-8")
        Log("日志已导出到: " selected)
        
        ; 询问是否打开所在文件夹
        result := MsgBox("日志已成功保存到：`n" selected "`n`n是否打开所在文件夹？", "导出成功", "YesNo Iconi")
        if (result = "Yes") {
            ; 打开文件夹并选中文件
            Run('explorer /select, "' selected '"')
        }
    } catch as err {
        Log("导出日志失败: " err.Message)
        MsgBox("导出失败: " err.Message, "错误", "IconX")
    }
}

; ========== 热键注册（可自定义） ==========
RegisterHotkeys() {
    global hkFront, hkMin, hkSize
    try Hotkey(hkFront, "Off")
    try Hotkey(hkMin, "Off")
    try Hotkey(hkSize, "Off")
    hkFront := FriendlyToHotkey(edHotkeyFront.Value)
    hkMin := FriendlyToHotkey(edHotkeyMin.Value)
    hkSize := FriendlyToHotkey(edHotkeySize.Value)
    try Hotkey(hkFront, BringAllGamesToFront)
    catch as err
        Log("热键注册失败(前置全部): " err.Message)
    try Hotkey(hkMin, MinimizeAllGames)
    catch as err
        Log("热键注册失败(老板键): " err.Message)
    try Hotkey(hkSize, GetActiveWindowSize)
    catch as err
        Log("热键注册失败(获取窗口大小): " err.Message)
    Log("热键: 前置=" hkFront " 老板键=" hkMin " 获取大小=" hkSize)
}

; ========== 启动 ==========
LoadConfig()
RegisterHotkeys()
Log("启动完成，等待用户操作...")