using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Minimal parameterized libpq adapter. libpq is PostgreSQL's official native client.
// Each request owns one connection; native handles and results have deterministic lifetimes.
sealed class PgException(string state,string message):Exception(message){public string SqlState=>state;}
sealed class PgDataSource(string connection){
 public static PgDataSource Create(string connection)=>new(connection);
 public Task<PgConnection> OpenConnectionAsync()=>Task.FromResult(new PgConnection(connection));
}
sealed class PgConnection:IAsyncDisposable{
 readonly IntPtr handle;
 static PgConnection(){
  NativeLibrary.SetDllImportResolver(typeof(PgConnection).Assembly,(name,assembly,path)=>{
   if(name!="libpq")return IntPtr.Zero;
   var dir=Environment.GetEnvironmentVariable("PG_LIB_DIR");
   if(!string.IsNullOrEmpty(dir))return NativeLibrary.Load(Path.Combine(dir,OperatingSystem.IsWindows()?"libpq.dll":"libpq.so.5"));
   return NativeLibrary.Load(OperatingSystem.IsWindows()?"libpq.dll":"libpq.so.5",assembly,path);
  });
 }
 public PgConnection(string input){
  var connection=input;
  if(!input.StartsWith("postgresql://")&&!input.StartsWith("postgres://")){
   var keys=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Host","host"},{"Port","port"},{"Database","dbname"},{"Username","user"},{"Password","password"},{"SSL Mode","sslmode"}};
   var parts=new System.Data.Common.DbConnectionStringBuilder{ConnectionString=input};
   connection=string.Join(" ",parts.Keys.Cast<string>().Where(keys.ContainsKey).Select(k=>keys[k]+"='"+Convert.ToString(parts[k],CultureInfo.InvariantCulture)!.Replace("\\","\\\\").Replace("'","\\'")+"'"));
  }
  handle=Native.PQconnectdb(connection.StartsWith("postgres")?connection:connection+" connect_timeout=10");
  if(handle==IntPtr.Zero||Native.PQstatus(handle)!=0){var error=handle==IntPtr.Zero?"Cannot allocate PostgreSQL connection":Native.Text(Native.PQerrorMessage(handle));if(handle!=IntPtr.Zero)Native.PQfinish(handle);throw new Exception(error);}
 }
 public Task<PgTransaction> BeginTransactionAsync(){Run("BEGIN",[]);return Task.FromResult(new PgTransaction(this));}
 public ValueTask DisposeAsync(){Native.PQfinish(handle);return ValueTask.CompletedTask;}
 public (List<Dictionary<string,object?>> rows,int affected) Run(string sql,(string,object)[] args){
  var values=new List<IntPtr>();var positions=new Dictionary<string,int>();
  try{
   foreach(var (key,value) in args){positions[key]=values.Count+1;var text=value switch{bool b=>b?"true":"false",Guid g=>g.ToString(),_=>Convert.ToString(value,CultureInfo.InvariantCulture)??""};values.Add(Marshal.StringToCoTaskMemUTF8(text));}
   sql=Regex.Replace(sql,@"@([A-Za-z_][A-Za-z0-9_]*)",m=>positions.TryGetValue(m.Groups[1].Value,out var index)?"$"+index:m.Value);
   var result=args.Length==0?Native.PQexec(handle,sql):Native.PQexecParams(handle,sql,args.Length,IntPtr.Zero,values.ToArray(),IntPtr.Zero,IntPtr.Zero,0);
   if(result==IntPtr.Zero)throw new Exception(Native.Text(Native.PQerrorMessage(handle)));
   try{
    var status=Native.PQresultStatus(result);if(status is not (1 or 2))throw new PgException(Native.Text(Native.PQresultErrorField(result,67)),Native.Text(Native.PQresultErrorMessage(result)));
    var rows=new List<Dictionary<string,object?>>();var columns=Native.PQnfields(result);
    for(int r=0;r<Native.PQntuples(result);r++){
     var row=new Dictionary<string,object?>();
     for(int i=0;i<columns;i++){
      var parts=Native.Text(Native.PQfname(result,i)).Split('_');var key=parts[0]+string.Concat(parts.Skip(1).Select(p=>char.ToUpperInvariant(p[0])+p[1..]));
      object? value=null;
      if(Native.PQgetisnull(result,r,i)==0){var text=Native.Text(Native.PQgetvalue(result,r,i));value=Native.PQftype(result,i) switch{16=>text=="t",20=>long.Parse(text,CultureInfo.InvariantCulture),21 or 23=>int.Parse(text,CultureInfo.InvariantCulture),2950=>Guid.Parse(text),1184=>DateTimeOffset.Parse(text,CultureInfo.InvariantCulture),_=>text};}
      row[key]=value;
     }
     rows.Add(row);
    }
    int.TryParse(Native.Text(Native.PQcmdTuples(result)),out var affected);return(rows,affected);
   }finally{Native.PQclear(result);}
  }finally{foreach(var value in values)Marshal.FreeCoTaskMem(value);}
 }
}
sealed class PgTransaction(PgConnection connection):IAsyncDisposable{
 bool committed;
 public Task CommitAsync(){connection.Run("COMMIT",[]);committed=true;return Task.CompletedTask;}
 public ValueTask DisposeAsync(){if(!committed)connection.Run("ROLLBACK",[]);return ValueTask.CompletedTask;}
}
static class Native{
 const string Lib="libpq";
 internal static string Text(IntPtr p)=>Marshal.PtrToStringUTF8(p)??"";
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQconnectdb([MarshalAs(UnmanagedType.LPUTF8Str)]string connection);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern int PQstatus(IntPtr c);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQerrorMessage(IntPtr c);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern void PQfinish(IntPtr c);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQexec(IntPtr c,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQexecParams(IntPtr c,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int count,IntPtr types,IntPtr[] values,IntPtr lengths,IntPtr formats,int resultFormat);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern int PQresultStatus(IntPtr result);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQresultErrorMessage(IntPtr result);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQresultErrorField(IntPtr result,int code);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern int PQnfields(IntPtr result);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern int PQntuples(IntPtr result);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQfname(IntPtr result,int col);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern uint PQftype(IntPtr result,int col);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern int PQgetisnull(IntPtr result,int row,int col);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQgetvalue(IntPtr result,int row,int col);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr PQcmdTuples(IntPtr result);
 [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)]internal static extern void PQclear(IntPtr result);
}

