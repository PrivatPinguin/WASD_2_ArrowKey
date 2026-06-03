Imports System
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Linq

Module Program
		 Private TargetGameNames As String() = {}

		 Private Const WH_KEYBOARD_LL As Integer = 13
		 Private Const WM_KEYDOWN As Integer = &H100
		 Private Const WM_KEYUP As Integer = &H101
		 Private Const WM_SYSKEYDOWN As Integer = &H104
		 Private Const WM_SYSKEYUP As Integer = &H105

		 Private Const VK_W As Integer = &H57
		 Private Const VK_A As Integer = &H41
		 Private Const VK_S As Integer = &H53
		 Private Const VK_D As Integer = &H44

		 Private Const VK_UP As Integer = &H26
		 Private Const VK_LEFT As Integer = &H25
		 Private Const VK_DOWN As Integer = &H28
		 Private Const VK_RIGHT As Integer = &H27

		 Private Const LLKHF_INJECTED As UInteger = &H10UI

		 ' Fokus-Cache: Ergebnis nur neu berechnen wenn sich das Vordergrundfenster ändert
		 Private s_cachedHwnd As IntPtr = IntPtr.Zero
		 Private s_cachedResult As Boolean = False

		 Private hookHandle As IntPtr = IntPtr.Zero
		 Private hookProc As LowLevelKeyboardProc = AddressOf KeyboardProc

		 Sub Main(args As String())
					Console.WriteLine("Ersetzt WASD zu Pfeiltasten." & Environment.NewLine)
					' games.txt laden
					Try
							 Console.WriteLine("Lade games.txt mit deinen Spielen.")
							 If System.IO.File.Exists("games.txt") Then
										Dim lines = System.IO.File.ReadAllLines("games.txt")
										' Leere Zeilen und Kommentare (mit #) filtern
										Dim gameList As New List(Of String)
										For Each line In lines
												 Dim trimmed = line.Trim()
												 If Not String.IsNullOrWhiteSpace(trimmed) AndAlso Not trimmed.StartsWith("#") Then
															gameList.Add(trimmed)
												 End If
										Next
										TargetGameNames = gameList.ToArray()

										Console.WriteLine($"games.txt geladen: {TargetGameNames.Length} Spiele")
										For Each game In TargetGameNames
												 Console.WriteLine($"  - {game}")
										Next
							 Else
										Console.WriteLine("Fehler: games.txt nicht gefunden!")
										Return
							 End If
					Catch ex As Exception
							 Console.WriteLine($"Fehler beim Laden von games.txt: {ex.Message}")
							 Return
					End Try

					' Explizites Modul-Handle für Kompatibilität mit .NET 5+
					Dim hMod As IntPtr
					Using curProcess = Process.GetCurrentProcess()
							 Using curModule = curProcess.MainModule
										hMod = GetModuleHandle(curModule.ModuleName)
							 End Using
					End Using

					hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, hMod, 0)

					If hookHandle = IntPtr.Zero Then
							 'Console.WriteLine("Hook konnte nicht gesetzt werden.")
							 Return
					End If

					AddHandler AppDomain.CurrentDomain.ProcessExit, AddressOf OnProcessExit

					'Console.WriteLine("Aktiv: W/A/S/D -> Pfeiltasten bei Zielspiel im Fokus.")
					'Console.WriteLine("Beenden mit Ctrl+C.")

					' WH_KEYBOARD_LL benötigt eine Message-Loop auf dem installierenden Thread.
					' Console.ReadLine() pumpt keine Messages → Windows entfernt den Hook nach ~5 s.
					AddHandler Console.CancelKeyPress, AddressOf OnCancelKey
					RunMessageLoop()

					UnhookWindowsHookEx(hookHandle)
		 End Sub

		 Private Sub OnCancelKey(sender As Object, e As ConsoleCancelEventArgs)
					e.Cancel = True   ' Prozess nicht sofort beenden – sauber via PostQuitMessage
					PostQuitMessage(0)
		 End Sub

		 Private Sub RunMessageLoop()
					Dim msg As MSG
					Dim ret As Integer = GetMessage(msg, IntPtr.Zero, 0, 0)
					Do While ret <> 0
							 If ret = -1 Then Exit Do   ' Fehler
							 TranslateMessage(msg)
							 DispatchMessage(msg)
							 ret = GetMessage(msg, IntPtr.Zero, 0, 0)
					Loop
		 End Sub

		 Private Sub OnProcessExit(sender As Object, e As EventArgs)
					If hookHandle <> IntPtr.Zero Then
							 UnhookWindowsHookEx(hookHandle)
							 hookHandle = IntPtr.Zero
					End If
		 End Sub

		 Private Function KeyboardProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
					If nCode >= 0 Then
							 Dim info = Marshal.PtrToStructure(Of KBDLLHOOKSTRUCT)(lParam)

							 ' Injizierte Events ignorieren
							 If (info.flags And LLKHF_INJECTED) = 0UI Then
										Dim mappedKey = MapWASDToArrow(CInt(info.vkCode))
										If mappedKey <> 0 Then
												 Dim isFocused = IsTargetGameFocusedCached()
												 'Console.WriteLine($"WASD Key {info.vkCode:X2} -> Arrow {mappedKey:X2}, Focused: {isFocused}, hwnd: {s_cachedHwnd}")

												 If isFocused Then
															Dim msg = CInt(wParam)
															If msg = WM_KEYDOWN OrElse msg = WM_SYSKEYDOWN Then
																	 'Console.WriteLine($"  -> KEYDOWN")
																	 SendVirtualKey(s_cachedHwnd, mappedKey, False)
																	 Return CType(1, IntPtr)
															ElseIf msg = WM_KEYUP OrElse msg = WM_SYSKEYUP Then
																	 'Console.WriteLine($"  -> KEYUP")
																	 SendVirtualKey(s_cachedHwnd, mappedKey, True)
																	 Return CType(1, IntPtr)
															End If
												 End If
										End If
							 End If
					End If

					Return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam)
		 End Function

		 Private Function MapWASDToArrow(vk As Integer) As Integer
					Select Case vk
							 Case VK_W : Return VK_UP
							 Case VK_A : Return VK_LEFT
							 Case VK_S : Return VK_DOWN
							 Case VK_D : Return VK_RIGHT
							 Case Else : Return 0
					End Select
		 End Function

		 ''' <summary>
		 ''' Prüft den Fokus nur neu, wenn sich das Vordergrundfenster geändert hat.
		 ''' Verhindert wiederholte Process- und Titel-Abfragen bei schnellen Tastenschlägen.
		 ''' </summary>
		 Private Function IsTargetGameFocusedCached() As Boolean
					Dim foreground = GetForegroundWindow()

					' Fenster unverändert → gecachtes Ergebnis zurückgeben (kein API-Overhead)
					If foreground = s_cachedHwnd Then
							 Return s_cachedResult
					End If

					s_cachedHwnd = foreground
					s_cachedResult = EvaluateFocus(foreground)
					Return s_cachedResult
		 End Function

		 Private Function EvaluateFocus(foreground As IntPtr) As Boolean
					If foreground = IntPtr.Zero OrElse IsIconic(foreground) Then
							 Return False
					End If

					' Prozessname bevorzugen (stabiler als Fenstertitel), Titel als Fallback
					Dim pid As UInteger
					GetWindowThreadProcessId(foreground, pid)

					Dim processName As String = String.Empty
					If pid <> 0UI Then
							 Try
										Using proc = Process.GetProcessById(CInt(pid))
												 processName = proc.ProcessName
										End Using
							 Catch
							 End Try
					End If

					If MatchesTarget(processName) Then Return True

					' Fenstertitel nur wenn Prozessname nicht gematcht hat
					Return MatchesTarget(GetWindowTitle(foreground))
		 End Function

		 Private Function MatchesTarget(value As String) As Boolean
					If String.IsNullOrEmpty(value) Then Return False

					For Each target In TargetGameNames
							 If Not String.IsNullOrEmpty(target) AndAlso
									value.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 Then
										Return True
							 End If
					Next

					Return False
		 End Function

		 Private Function GetWindowTitle(hWnd As IntPtr) As String
					Dim length = GetWindowTextLength(hWnd)
					If length <= 0 Then Return String.Empty

					Dim sb As New Text.StringBuilder(length + 1)
					GetWindowText(hWnd, sb, sb.Capacity)
					Return sb.ToString()
		 End Function

		 ''' <summary>
		 ''' Sendet Tastendruck direkt via keybd_event.
		 ''' Funktioniert besser bei DirectInput/Raw-Input Games.
		 ''' </summary>
		 Private Sub SendVirtualKey(hWnd As IntPtr, vk As Integer, keyUp As Boolean)
					Dim scanCode = MapVirtualKey(CUInt(vk), 0)

					' Extended-Key-Flag für Pfeiltasten
					Dim isExtendedKey = (vk = VK_UP OrElse vk = VK_LEFT OrElse vk = VK_DOWN OrElse vk = VK_RIGHT)
					Dim flags As Byte = 0
					If isExtendedKey Then
							 flags = flags Or 1 ' KEYEVENTF_EXTENDEDKEY
					End If
					If keyUp Then
							 flags = flags Or 2 ' KEYEVENTF_KEYUP
					End If

					keybd_event(CByte(vk), CByte(scanCode), flags, IntPtr.Zero)
					Dim eventType = If(keyUp, "KEYUP", "KEYDOWN")
					'Console.WriteLine($"    keybd_event(vk={vk:X2} scan={scanCode:X2} extended={isExtendedKey} {eventType}) flags={flags:X2}")
		 End Sub

		 Private Delegate Function LowLevelKeyboardProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr

		 <StructLayout(LayoutKind.Sequential)>
		 Private Structure KBDLLHOOKSTRUCT
					Public vkCode As UInteger
					Public scanCode As UInteger
					Public flags As UInteger
					Public time As UInteger
					Public dwExtraInfo As IntPtr
		 End Structure

		 ' Sequential: CLR fügt Padding nach type (4 Byte) automatisch ein → ki bei Offset 8 auf 64-bit
		 ' sizeof(INPUT) = 32 auf 64-bit, 20 auf 32-bit – identisch mit Windows-Definition
		 <StructLayout(LayoutKind.Sequential)>
		 Private Structure INPUT
					Public type As UInteger
					Public ki As KEYBDINPUT
		 End Structure

		 <StructLayout(LayoutKind.Sequential)>
		 Private Structure KEYBDINPUT
					Public wVk As UShort
					Public wScan As UShort
					Public dwFlags As UInteger
					Public time As UInteger
					Public dwExtraInfo As IntPtr
		 End Structure

		 <DllImport("user32.dll", SetLastError:=True)>
		 Private Function SetWindowsHookEx(idHook As Integer, lpfn As LowLevelKeyboardProc, hMod As IntPtr, dwThreadId As UInteger) As IntPtr
		 End Function

		 <DllImport("user32.dll", SetLastError:=True)>
		 Private Function UnhookWindowsHookEx(hhk As IntPtr) As Boolean
		 End Function

		 <DllImport("user32.dll", SetLastError:=True)>
		 Private Function CallNextHookEx(hhk As IntPtr, nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
		 End Function

		 <DllImport("kernel32.dll", CharSet:=CharSet.Auto, SetLastError:=True)>
		 Private Function GetModuleHandle(lpModuleName As String) As IntPtr
		 End Function

		 <DllImport("user32.dll")>
		 Private Function GetForegroundWindow() As IntPtr
		 End Function

		 <DllImport("user32.dll")>
		 Private Function GetWindowThreadProcessId(hWnd As IntPtr, ByRef lpdwProcessId As UInteger) As UInteger
		 End Function

		 <DllImport("user32.dll")>
		 Private Function IsIconic(hWnd As IntPtr) As Boolean
		 End Function

		 <DllImport("user32.dll")>
		 Private Function PostMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As Boolean
		 End Function

		 <DllImport("user32.dll", SetLastError:=True)>
		 Private Function SendInput(nInputs As UInteger, pInputs As INPUT, cbSize As Integer) As UInteger
		 End Function

		 <DllImport("user32.dll")>
		 Private Sub keybd_event(bVk As Byte, bScan As Byte, dwFlags As Byte, dwExtraInfo As IntPtr)
		 End Sub

		 <DllImport("user32.dll")>
		 Private Function MapVirtualKey(uCode As UInteger, uMapType As UInteger) As UInteger
		 End Function

		 <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
		 Private Function GetWindowText(hWnd As IntPtr, lpString As Text.StringBuilder, nMaxCount As Integer) As Integer
		 End Function

		 <DllImport("user32.dll")>
		 Private Function GetWindowTextLength(hWnd As IntPtr) As Integer
		 End Function

		 <StructLayout(LayoutKind.Sequential)>
		 Private Structure MSG
					Public hwnd As IntPtr
					Public message As UInteger
					Public wParam As IntPtr
					Public lParam As IntPtr
					Public time As UInteger
					Public pt As POINT
		 End Structure

		 <StructLayout(LayoutKind.Sequential)>
		 Private Structure POINT
					Public x As Integer
					Public y As Integer
		 End Structure

		 <DllImport("user32.dll")>
		 Private Function GetMessage(ByRef lpMsg As MSG, hWnd As IntPtr, wMsgFilterMin As UInteger, wMsgFilterMax As UInteger) As Integer
		 End Function

		 <DllImport("user32.dll")>
		 Private Function TranslateMessage(ByRef lpMsg As MSG) As Boolean
		 End Function

		 <DllImport("user32.dll")>
		 Private Function DispatchMessage(ByRef lpMsg As MSG) As IntPtr
		 End Function

		 <DllImport("user32.dll")>
		 Private Sub PostQuitMessage(nExitCode As Integer)
		 End Sub
End Module
