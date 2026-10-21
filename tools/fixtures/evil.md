# 不該執行任何腳本

如果下面任何一個執行了，視窗標題會變成 PWNED。

<script>parent.document.title = "PWNED"; document.title = "PWNED";</script>

<img src="nope.png" onerror="parent.document.title='PWNED'">

[點我](javascript:parent.document.title='PWNED')
