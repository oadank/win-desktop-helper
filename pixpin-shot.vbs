' shot-service 启动器 (计划任务 dsh-pixpin-shot 调用)
' 作用: 由任务计划程序拉起 shot-service, 进程脱离 AI 会话/终端进程树, 不会被连带杀掉
' 部署后拉起方式: schtasks /run /tn "dsh-pixpin-shot"  (禁止 AI 用 Start-Process 直拉)
CreateObject("Wscript.Shell").Run """C:\Users\oadan\AppData\Local\Programs\win-desktop-helper\shot-service.exe""", 0, False
