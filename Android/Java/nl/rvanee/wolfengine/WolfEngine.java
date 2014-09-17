package nl.rvanee.wolfengine;

import org.libsdl.app.SDLActivity;
import android.os.*; 
import android.util.Log;

public class WolfEngine extends SDLActivity
{
	public native void Pause();
	public native void Resume();
	
	protected void onCreate(Bundle savedInstanceState) { 
        super.onCreate(savedInstanceState); 
    } 
    
    protected void onDestroy() { 
        super.onDestroy(); 
    } 
	
	protected void onPause() {
		super.onPause();
		Pause();
	}
	
	protected void onResume() {
		super.onResume();
		Resume();
	}
}