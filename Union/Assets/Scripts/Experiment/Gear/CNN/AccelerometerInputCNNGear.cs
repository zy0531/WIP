using UnityEngine;
using System.Collections;
using UnityEngine.XR;
using System.IO;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;
using UnityEngine.UI;
using System.Threading;

using TensorFlow;


public class AccelerometerInputCNNGear : MonoBehaviour
{
    // set per person
    public float height = 1.75f;

    Thread run;
    Thread collect;

    // used to determine direction to walk
    private float yaw;
    private float rad;
    private float xVal;
    private float zVal;

    // determine if person is picking up speed or slowing down
    public static float velocity = 0f;
    public static float method1StartTimeGrow = 0f;
    public static float method1StartTimeDecay = 0f;
    //phase one when above (+/-) 0.10 threshold
    public static bool wasOne = false;
    //phase two when b/w -0.10 and 0.10 thresholds
    public static bool wasTwo = true;
    private float decayRate = 0.4f;

    // initial X and Y angles - used to determine if user is looking around
    private float eulerX;
    private float eulerZ;

    // indicates if person is looking around - not implemented yet
    bool looking = false;

    // set by trained CNN model
    public int inputWidth = 60;

    // third value corresponds to inputWidth
    public TextAsset graphModel;
    private float[,,,] inputTensor = new float[1, 1, 60, 3];

    // list for keeping track of values for tensor
    private List<float> accelX;
    private List<float> accelY;
    private List<float> accelZ;

    // list for smoothing out cnn output
    private List<float> latch;
    int latchSum = 0;
    int latchWidth = 30;

    // determine if person is walking from cnn returned value
    private bool walking = false;
    private int standIndex = 0;
    //private int lookIndex = 2;

    // how many options of activities we have - standing, walking
    private int activityIndexChoices = 2;

    // FOR DEBUGGING PUTTING AT GLOBAL SCOPE
    float confidence = 0;
    float sum = 0f;
    float test = 0f;
    int activity = 0;
    bool here = false;
    bool longTime = false;
    float line = 0f;
    int index = 0;
    int countCNN = 0;
    float total = 0;
    float test1 = 0f;
    float test2 = 0f;
    float test3 = 0f;
    bool one = true;

    int diff = 20;

    void Start()
    {
        // tensorflowsharp requires this statement
#if UNITY_ANDROID
		TensorFlowSharp.Android.NativeBinding.Init ();
#endif
        // enable the gyroscope on the phone
        Input.gyro.enabled = true;

        // if we are on the right VR, then setup a client device to read transform data from
        if (Application.platform == RuntimePlatform.Android)
            SetupClient();

        // user must be looking ahead at the start
        eulerX = InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.x;
        eulerZ = InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.z;

        // initialize the cnn queues
        accelX = new List<float>();
        accelY = new List<float>();
        accelZ = new List<float>();
        latch = new List<float>();

        // start collection thread
        collect = new Thread(manageCollection);
        collect.Start();

        // start cnn thread - different thread so cnn doesn't interfere with graphics
        run = new Thread(manageCNN);
        run.Start();
    }

    void FixedUpdate()
    {
        // send the current transform data to the server (should probably be wrapped in an if isAndroid but I haven't tested)

        string path = Application.persistentDataPath + "/WIP_looking.txt";

        // debugging output
        string appendText = "\r\n" + String.Format("{0,20} {1,7} {2, 15} {3, 15} {4, 15} {5, 15} {6, 15} {7, 8} {8, 10} {9, 10} {10, 10} {11, 10} {12,10} {13,10} {14,10} {15,10} {16,10} {17,10} {18,10} {19,10} {20,10}",
                                DateTime.Now.ToString(), Time.time,

                                Input.gyro.userAcceleration.x, 
			                    Input.gyro.userAcceleration.y, 
			                    Input.gyro.userAcceleration.z,

                                InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.x,
                                InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.y,
                                InputTracking.GetLocalRotation(XRNode.Head).eulerAngles.z,

                                confidence, sum, test, index, here, accelX.Count, countCNN, total, diff,

                                line, test1, test2, latchSum);

        File.AppendAllText(path, appendText);

        // do the movement algorithm, more details inside
        move();

        if (myClient != null)
            myClient.Send(MESSAGE_DATA, new TDMessage(this.transform.localPosition, Camera.main.transform.eulerAngles));
    }

    void OnApplicationQuit()
    {
        collect.Abort();
        run.Abort();
    }

    // manages the accelerometer data collection thread
    void manageCollection()
    {
        // thread sleep for 1000 as to not connect information on boot
        Thread.Sleep(1000);
        while (true)
        {
            // sleeping time is linked to collection time
            Thread.Sleep(5);
            collectValues();
        }
    }

    // poll the Gear for gyro data
    void collectValues()
    {
        float currX = Input.gyro.userAcceleration.x;
        float currY = Input.gyro.userAcceleration.y;
        float currZ = Input.gyro.userAcceleration.z;
        test = currY;

        // collect - want the list to always be inputWidth
        if (accelX.Count < inputWidth)
        {
            accelX.Add(currX);
            accelY.Add(currY);
            accelZ.Add(currZ);
        }
        if (accelX.Count == inputWidth)
        {
            accelX.RemoveAt(0);
            accelY.RemoveAt(0);
            accelZ.RemoveAt(0);
            accelX.Add(currX);
            accelY.Add(currY);
            accelZ.Add(currZ);
        }
        line = currY;
    }

    // thread to manage the CNN
    void manageCNN()
    {

        while(true)
        {
            // sleeps so doesn't run while application is booting
            Thread.Sleep(50);

            // time is for debugging
            float prev = Time.time;
            
            // run the cnn
            evaluate();

            float len = Time.time - prev;
            diff = (int)((0.5 - len)*1000);
            if(diff < 0)
            {
                diff *= -1;
            }
        }
    }

    // run the CNN
    void evaluate ()
	{
        // only run CNN if we have enough accelerometer values 
        if (accelX.Count == inputWidth)
        {
            // convert from list to tensor
            // if tensor is 1 under, add dummy last value
            int i;
            for (i = 0; i < accelX.Count; i++)
            {
                inputTensor[0, 0, i, 0] = accelX[i];
                test = inputTensor[0, 0, i, 0];
            }
            if (i != inputWidth)
            {
                inputTensor[0, 0, inputWidth - 1, 0] = 0;
            }

            for (i = 0; i < accelY.Count; i++)
            {
                inputTensor[0, 0, i, 1] = accelY[i];
            }
            if (i != inputWidth)
            {
                inputTensor[0, 0, inputWidth - 1, 1] = 0;
            }

            for (i = 0; i < accelZ.Count; i++)
            {
                inputTensor[0, 0, i, 2] = accelZ[i];
            }
            if (i != inputWidth)
            {
                inputTensor[0, 0, inputWidth - 1, 2] = 0;
            }

            // tensor output variable
            float[,] recurrentTensor;

            // create tensorflow model
            using (var graph = new TFGraph())
            {
                graph.Import(graphModel.bytes);
                var session = new TFSession(graph);
                var runner = session.GetRunner();

                // do input tensor list to array and make it one dimensional
                TFTensor input = inputTensor;


                // set up input tensor and input
                runner.AddInput(graph["input_placeholder_x"][0], input);

                // set up output tensor
                runner.Fetch(graph["output_node"][0]);

                // run model
                recurrentTensor = runner.Run()[0].GetValue() as float[,];
                here = true;

                // dispose resources - keeps cnn from breaking down later
                session.Dispose();
                graph.Dispose();

            }

            // find the most confident answer
            float highVal = 0;
            int highInd = -1;
            sum = 0f;

            // *MAKE SURE ACTIVITYINDEXCHOICES MATCHES THE NUMBER OF CHOICES*
            for (int j = 0; j < activityIndexChoices; j++)
            {

                confidence = recurrentTensor[0, j];
                if (highInd > -1)
                {
                    if (recurrentTensor[0, j] > highVal)
                    {
                        highVal = confidence;
                        highInd = j;
                    }
                }
                else
                {
                    highVal = confidence;
                    highInd = j;
                }

                // debugging - sum should = 1 at the end
                sum += confidence;
            }

            // debugging
            test1 = recurrentTensor[0, 0];
            test2 = recurrentTensor[0, 1];

            // used in movement to see if we should be moving
            index = highInd;
            countCNN++;
        }
       
    }

	// algorithm to determine if the user is looking around. Looking and walking generate similar gyro.accelerations, so we
	//want to ignore movements that could be spawned from looking around. Makes sure user's head orientation is in certain window
	bool look (double start, double curr, double diff)
	{
		//Determines if the user's current angle (curr) is within the window (start +/- diff)
		//Deals with wrap around values (eulerAngles is in range 0 to 360)
		if ((start + diff) > 360f) {
			if (((curr >= 0f) && (curr <= (start + diff - 360f))) || ((((start - diff) <= curr) && (curr <= 360f)))) {
				return false;
			}
		} else if ((start - diff) < 0f) {
			if (((0f <= curr) && (curr <= (start + diff))) || (((start - diff + 360f) <= curr) && (curr <= 360f))) {
				return false;
			}
		} else if (((start + diff) <= curr) && (curr <= (start + diff))) {
			return false;
		}
		return true;
	}

	// if the user is walking, moves them in correct direction with varying velocities
	// also sets velocity to 0 if it is determined that the user is no longer walking
	void move ()
	{
		//Get the yaw of the subject to allow for movement in the look direction
		yaw = InputTracking.GetLocalRotation (XRNode.Head).eulerAngles.y;
		//convert that value into radians because math uses radians
		rad = yaw * Mathf.Deg2Rad;
		//map that value onto the unit circle to faciliate movement in the look direction
		zVal = Mathf.Cos (rad);
		xVal = Mathf.Sin (rad);
    	
    	bool looking = (look (eulerX, InputTracking.GetLocalRotation (XRNode.Head).eulerAngles.x, 20f) || look (eulerZ, InputTracking.GetLocalRotation (XRNode.Head).eulerAngles.z, 15f));

        // smooth the cnn output 
        // can have a large latch value bc exponential walking will decrease to 0 for us
        if (latch.Count < latchWidth)
        {
            latch.Add(index);
            latchSum += index;
        } else
        {
            latchSum -= (int)latch[0];
            latch.RemoveAt(0);
            latchSum += index;
            latch.Add(index);
        }
        
        // use cnn to determine if walking
        if (index != standIndex || latchSum > 0) {
			walking = true;
		} else {
      			walking = false;
    	}

        // check looking condition and use exponential values to mimic walking cadence
        if (!walking || looking)
        {
            velocity = 0f;
        } else
        {
            if ((Input.gyro.userAcceleration.y >= 0.075f || Input.gyro.userAcceleration.y <= -0.075f))
            {
                if (wasTwo)
                { //we are transitioning from phase 2 to 1
                    method1StartTimeGrow = Time.time;
                    wasTwo = false;
                    wasOne = true;
                }
            }
            else
            {
                if (wasOne)
                {
                    method1StartTimeDecay = Time.time;
                    wasOne = false;
                    wasTwo = true;
                }
            }

            //Movement is done exponentially. We want the user to quickly accelerate and quickly decelerate as to minimize
            //starting and stopping latency.
            if ((Input.gyro.userAcceleration.y >= 0.075f || Input.gyro.userAcceleration.y <= -0.075f))
            {
                velocity = 2.5f - (2.5f - velocity) * Mathf.Exp((method1StartTimeGrow - Time.time) / 0.2f); //grow
            }
            else
            {
                velocity = 0.0f - (0.0f - velocity) * Mathf.Exp((method1StartTimeDecay - Time.time) / decayRate); //decay
            }
        }

        //Multiply intended speed (called velocity) by delta time to get a distance, then multiply that distamce
        //    by the unit vector in the look direction to get displacement.
        transform.Translate (xVal * velocity * Time.fixedDeltaTime, 0, zVal * velocity * Time.fixedDeltaTime);
	}

	#region NetworkingCode

	//Declare a client node
	NetworkClient myClient;
	//Define two types of data, one for setup (unused) and one for actual data
	const short MESSAGE_DATA = 880;
	const short MESSAGE_INFO = 881;
	//Server address is Flynn, tracker address is Baines, port is for broadcasting
	const string SERVER_ADDRESS = "192.168.1.2";
	const string TRACKER_ADDRESS = "192.168.1.100";
	const int SERVER_PORT = 5000;

	//Message and message text are now depreciated, were used for debugging
	public string message = "";
	public Text messageText;

	//Connection ID for the client server interaction
	public int _connectionID;
	//transform data that is being read from the clien
	public static Vector3 _pos = new Vector3 ();
	public static Vector3 _euler = new Vector3 ();

	// Create a client and connect to the server port
	public void SetupClient ()
	{
		myClient = new NetworkClient (); //Instantiate the client
		myClient.RegisterHandler (MESSAGE_DATA, DataReceptionHandler); //Register a handler to handle incoming message data
		myClient.RegisterHandler (MsgType.Connect, OnConnected); //Register a handler to handle a connection to the server (will setup important info
		myClient.Connect (SERVER_ADDRESS, SERVER_PORT); //Attempt to connect, this will send a connect request which is good if the OnConnected fires
	}

	// client function to recognized a connection
	public void OnConnected (NetworkMessage netMsg)
	{
		_connectionID = netMsg.conn.connectionId; //Keep connection id, not really neccesary I don't think
	}

	// Clinet function that fires when a disconnect occurs (probably unnecessary
	public void OnDisconnected (NetworkMessage netMsg)
	{
		_connectionID = -1;
	}

	//I actually don't know for sure if this is useful. I believe that this is erroneously put here and was duplicated in TDServer code.
	public void DataReceptionHandler (NetworkMessage _transformData)
	{
		TDMessage transformData = _transformData.ReadMessage<TDMessage> ();
		_pos = transformData._pos;
		_euler = transformData._euler;
	}

	#endregion
}
