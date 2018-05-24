# kate: space-indent off;

all: out-3.5/qwebchannel.dll out-4.0/qwebchannel.dll

CYGWINDIR = $(shell cygpath -m ${WINDIR})

NUGET = nuget.exe
CSC = $(CYGWINDIR)/Microsoft.NET/Framework/v3.5/csc.exe
CSC4 = $(CYGWINDIR)/Microsoft.NET/Framework/v4.0.30319/csc.exe
QWEBCHANNEL_SRC = src/QWebChannel.cs src/Transports.cs

.PHONY: clean

out-3.5/Newtonsoft.Json.dll:
	cd packages; $(NUGET) install
	mkdir -p out-3.5/
	cp packages/Newtonsoft.Json.10.0.3/lib/net35/Newtonsoft.Json.dll out-3.5/

out-4.0/Newtonsoft.Json.dll:
	cd packages; $(NUGET) install
	mkdir -p out-4.0/
	cp packages/Newtonsoft.Json.10.0.3/lib/net40/Newtonsoft.Json.dll out-4.0/

out-3.5/qwebchannel.dll: $(QWEBCHANNEL_SRC) out-3.5/Newtonsoft.Json.dll
	$(CSC) -nologo -out:`cygpath -w $@` -r:"out-3.5\Newtonsoft.Json.dll" -d:NET35 \
	       -t:library `cygpath -w $(QWEBCHANNEL_SRC)`

out-4.0/qwebchannel.dll: $(QWEBCHANNEL_SRC) out-4.0/Newtonsoft.Json.dll
	$(CSC4) -nologo -out:`cygpath -w $@` -r:"out-4.0\Newtonsoft.Json.dll" -d:NET40 \
	        -t:library `cygpath -w $(QWEBCHANNEL_SRC)`

clean:
	rm -rf out-3.5/
	rm -rf out-4.0/
